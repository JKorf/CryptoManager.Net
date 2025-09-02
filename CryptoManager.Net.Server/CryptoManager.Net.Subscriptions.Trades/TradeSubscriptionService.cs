﻿using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.Data;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CryptoManager.Net.Subscriptions.Trades
{
    public class TradeSubscriptionService
    {
        private readonly ILogger _logger;
        private readonly IExchangeSocketClient _socketClient;

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _symbolSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>();

        // ConnectionId => Subscriptions
        private ConcurrentDictionary<string, ConcurrentDictionary<string, ConnectionSubscription>> _connectionSubscriptions = new ConcurrentDictionary<string, ConcurrentDictionary<string, ConnectionSubscription>>();
        // TopicKey => UpdateSubscriptions
        private ConcurrentDictionary<string, TradeUpdateSubscription> _updateSubscriptions = new ConcurrentDictionary<string, TradeUpdateSubscription>();

        public int ConnectionCount => _connectionSubscriptions.Count;
        public int SubscriptionCount => _connectionSubscriptions.Sum(x => x.Value.Count);

        public TradeSubscriptionService(
            ILogger<TradeSubscriptionService> logger,
            IExchangeSocketClient socketClient)
        {
            _logger = logger;
            _socketClient = socketClient;
        }

        public async Task<CallResult> SubscribeAsync(
            string connectionId, 
            string symbolId,
            Action<ExchangeEvent<SharedTrade[]>> handler,
            Action<SubscriptionEvent> statusHandler,
            CancellationToken ct)
        {
            var symbolData = symbolId.Split("-");
            var client = _socketClient.GetTradeClient(TradingMode.Spot, symbolData[0]);
            if (client == null)
                return new CallResult(ArgumentError.Invalid(symbolId, $"No Trade subscription client available for {symbolData[0]}"));

            var semaphore = _symbolSemaphores.GetOrAdd(symbolId, x => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync();
            try
            {
                if (!_connectionSubscriptions.TryGetValue(connectionId, out var connectionSubs))
                {
                    connectionSubs = new ConcurrentDictionary<string, ConnectionSubscription>();
                    _connectionSubscriptions.TryAdd(connectionId, connectionSubs);
                }

                if (connectionSubs.TryGetValue(symbolId, out _))
                {
                    _logger.LogInformation("Connection already has a Trade subscription for {Symbol}", symbolId);
                    return CallResult.SuccessResult;
                }


                connectionSubs.TryAdd(symbolId, new ConnectionSubscription(symbolId, statusHandler, handler));

                if (_updateSubscriptions.TryGetValue(symbolId, out var existingSub))
                {
                    existingSub.SubscriptionCount++;
                    _logger.LogInformation("Adding callback to existing Trade subscription for {Symbol}, now {SubsOnSymbol}. {TotalSubs} Trade subscriptions for {DiffSubs} different symbols in total", symbolId, existingSub.SubscriptionCount, _connectionSubscriptions.Sum(x => x.Value.Count), _updateSubscriptions.Count);
                    return CallResult.SuccessResult;
                }

                _logger.LogInformation("Subscribing to Trade updates for {Symbol}", symbolId);
                var subscription = await client.SubscribeToTradeUpdatesAsync(new SubscribeTradeRequest(new SharedSymbol(TradingMode.Spot, symbolData[1], symbolData[2])), update => ProcessUpdate(symbolId, update));
                if (!subscription)
                {
                    _logger.LogWarning("Failed to subscribe Trade for {Symbol}: {Error}", symbolId, subscription.Error!.ToString());
                    return new CallResult(subscription.Error);
                }

                subscription.Data.ConnectionLost += () => ProcessConnectionLost(symbolId);
                subscription.Data.ConnectionRestored += x => ProcessConnectionRestored(symbolId);
                _updateSubscriptions.TryAdd(symbolId, new TradeUpdateSubscription(1, subscription.Data));
                _logger.LogInformation("Subscribed to Trade updates for {Symbol}, now {TotalSubs} Trade subscriptions for {DiffSubs} different symbols", symbolId, _connectionSubscriptions.Sum(x => x.Value.Count), _updateSubscriptions.Count);
                
            }
            finally
            {
                semaphore.Release();
            }

            return CallResult.SuccessResult;
        }

        private void ProcessConnectionRestored(string symbolId)
        {
            var evnt = new SubscriptionEvent(SubscriptionStatus.Restored);
            foreach (var updateSubscription in _connectionSubscriptions.SelectMany(x => x.Value).Where(x => x.Value.SymbolId == symbolId))
                updateSubscription.Value.StatusCallback(evnt);
        }
        private void ProcessConnectionLost(string symbolId)
        {
            var evnt = new SubscriptionEvent(SubscriptionStatus.Interrupted);
            foreach (var updateSubscription in _connectionSubscriptions.SelectMany(x => x.Value).Where(x => x.Value.SymbolId == symbolId))
                updateSubscription.Value.StatusCallback(evnt);
        }

        private void ProcessUpdate(string symbolId, ExchangeEvent<SharedTrade[]> update)
        {
            foreach (var updateSubscription in _connectionSubscriptions.SelectMany(x => x.Value).Where(x => x.Value.SymbolId == symbolId))
                updateSubscription.Value.DataCallback(update);
        }

        public async Task UnsubscribeAsync(string connectionId, string symbolId)
        {
            var semaphore = _symbolSemaphores[symbolId];
            await semaphore.WaitAsync();
            try
            {
                if (!_connectionSubscriptions.TryGetValue(connectionId, out var connectionSubscriptions))
                    return;

                if (!connectionSubscriptions.TryGetValue(symbolId, out var connectionSubscription))
                    return;

                connectionSubscriptions.Remove(symbolId, out _);

                if (!_updateSubscriptions.TryGetValue(symbolId, out var updateSubscription))
                    return;

                updateSubscription.SubscriptionCount -= 1;
                if (updateSubscription.SubscriptionCount > 0)
                {
                    _logger.LogInformation("Trade subscription removed for {Symbol}, now {SymbolSubs} subscription left on symbol. {TotalSubs} Trade subscriptions for {DiffSubs} different symbols", symbolId, updateSubscription.SubscriptionCount, _connectionSubscriptions.Sum(x => x.Value.Count), _updateSubscriptions.Count);
                    return;
                }

                await updateSubscription.UpdateSubscription.CloseAsync();
                _updateSubscriptions.Remove(symbolId, out _);
                _logger.LogInformation("Trade subscription for {Symbol} closed, now {TotalSubs} Trade subscriptions for {DiffSubs} different symbols", symbolId, _connectionSubscriptions.Sum(x => x.Value.Count), _updateSubscriptions.Count);
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task UnsubscribeAllAsync(string connectionId)
        {
            if (!_connectionSubscriptions.TryGetValue(connectionId, out var subscriptions))
                return;

            foreach(var sub in subscriptions)
                await UnsubscribeAsync(connectionId, sub.Value.SymbolId);
        }
    }
}
