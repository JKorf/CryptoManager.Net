using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.Data;
using CryptoManager.Net.Subscriptions.Trades;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CryptoManager.Net.Subscriptions.OrderBook
{
    public class OrderBookSubscriptionService
    {
        private readonly ILogger _logger;
        private readonly IExchangeOrderBookFactory _factory;

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _symbolSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>();

        // ConnectionId => Subscriptions
        private ConcurrentDictionary<string, ConcurrentDictionary<string, ConnectionSubscription>> _connectionSubscriptions = new ConcurrentDictionary<string, ConcurrentDictionary<string, ConnectionSubscription>>();
        // TopicKey => UpdateSubscriptions
        private ConcurrentDictionary<string, OrderBookUpdateSubscription> _updateSubscriptions = new ConcurrentDictionary<string, OrderBookUpdateSubscription>();

        public int ConnectionCount => _connectionSubscriptions.Count(x => x.Value.Count > 0);
        public int SubscriptionCount => _connectionSubscriptions.Sum(x => x.Value.Count);

        public OrderBookSubscriptionService(
            ILogger<OrderBookSubscriptionService> logger,
            IExchangeOrderBookFactory factory)
        {
            _logger = logger;
            _factory = factory;
        }

        public async Task<CallResult> SubscribeAsync(
            string connectionId,
            string symbolId,
            Action<DataEvent<SharedOrderBook>> dataHandler,
            Action<SubscriptionEvent> statusHandler,
            CancellationToken ct)
        {
            var symbolData = symbolId.Split("-");
            var orderBook = _factory.Create(symbolData[0], new SharedSymbol(TradingMode.Spot, symbolData[1], symbolData[2]), 10);
            if (orderBook == null)
                return new CallResult(ArgumentError.Invalid(symbolId, $"No Orderbook subscription client available for {symbolData[0]}"));

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
                    _logger.LogInformation("Connection already has a Orderbook subscription for {Symbol}", symbolId);
                    return CallResult.SuccessResult;
                }

                connectionSubs.TryAdd(symbolId, new ConnectionSubscription(symbolId, statusHandler, dataHandler));

                if (_updateSubscriptions.TryGetValue(symbolId, out var existingSub))
                {
                    existingSub.SubscriptionCount++;
                    _logger.LogInformation("Adding callback to existing Orderbook subscription for {Symbol}, now {SubsOnSymbol}. {TotalSubs} Orderbook subscriptions for {DiffSubs} different symbols in total", symbolId, existingSub.SubscriptionCount, _connectionSubscriptions.Sum(x => x.Value.Count), _updateSubscriptions.Count);
                    return CallResult.SuccessResult;
                }

                _logger.LogInformation("Subscribing to Orderbook updates for {Symbol}", symbolId);
                var result = await orderBook.StartAsync();
                if (!result)
                {
                    _logger.LogWarning("Failed to subscribe Orderbook for {Symbol}: {Error}", symbolId, result.Error!.ToString());
                    return new CallResult(result.Error);
                }

                var processTask = Task.Run(async () =>
                {
                    while (orderBook.Status != OrderBookStatus.Disconnected
                        && orderBook.Status != OrderBookStatus.Disposed)
                    {
                        var snapshot = orderBook.Book;
                        var sharedBook = new SharedOrderBook(snapshot.asks.Take(10).ToArray(), snapshot.bids.Take(10).ToArray());
                        ProcessUpdate(symbolId, new DataEvent<SharedOrderBook>(symbolData[0], sharedBook, DateTime.UtcNow, null).WithSymbol(symbolId));

                        await Task.Delay(100);
                    }
                });

                orderBook.OnStatusChange += (oldStatus, newStatus) => ProcessStatusUpdate(symbolId, oldStatus, newStatus, statusHandler);
                _updateSubscriptions.TryAdd(symbolId, new OrderBookUpdateSubscription(1, processTask, orderBook));
                _logger.LogInformation("Subscribed to Orderbook updates for {Symbol}, now {TotalSubs} Orderbook subscriptions for {DiffSubs} different symbols", symbolId, _connectionSubscriptions.Sum(x => x.Value.Count), _updateSubscriptions.Count);

            }
            finally
            {
                semaphore.Release();
            }

            return CallResult.SuccessResult;
        }

        private void ProcessStatusUpdate(string symbolId, OrderBookStatus oldState, OrderBookStatus newState, Action<SubscriptionEvent> statusHandler)
        {
            SubscriptionEvent? evnt = null;
            if (newState == OrderBookStatus.Synced)
                evnt = new SubscriptionEvent(StreamStatus.Restored);
            if (newState == OrderBookStatus.Reconnecting || (oldState == OrderBookStatus.Synced && newState == OrderBookStatus.Syncing))
                evnt = new SubscriptionEvent(StreamStatus.Interrupted);

            if (evnt == null)
                return;

            foreach (var updateSubscription in _connectionSubscriptions.SelectMany(x => x.Value).Where(x => x.Value.SymbolId == symbolId))
                updateSubscription.Value.StatusCallback(evnt);
        }

        private void ProcessUpdate(string symbolId, DataEvent<SharedOrderBook> update)
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
                    _logger.LogInformation("Orderbook subscription removed for {Symbol}, now {SymbolSubs} subscription left on symbol. {TotalSubs} Orderbook subscriptions for {DiffSubs} different symbols", symbolId, updateSubscription.SubscriptionCount, _connectionSubscriptions.Sum(x => x.Value.Count), _updateSubscriptions.Count);
                    return;
                }

                await updateSubscription.UpdateSubscription.StopAsync();
                await updateSubscription.UpdateTask;
                _updateSubscriptions.Remove(symbolId, out _);
                _logger.LogInformation("Orderbook subscription for {Symbol} closed, now {TotalSubs} Orderbook subscriptions for {DiffSubs} different symbols", symbolId, _connectionSubscriptions.Sum(x => x.Value.Count), _updateSubscriptions.Count);
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

            foreach (var sub in subscriptions)
                await UnsubscribeAsync(connectionId, sub.Value.SymbolId);

            _connectionSubscriptions.TryRemove(connectionId, out _);
        }
    }
}
