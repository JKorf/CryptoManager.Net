using CryptoClients.Net.Interfaces;
using CryptoClients.Net.Models;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CryptoManager.Net.Subscriptions.User
{
    public class SubscribeResult
    {
        public string Topic { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public bool Success => Error == null;
        public Error? Error { get; set; }
    }

    public class UserSubscriptionService
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _userSemaphores = new ConcurrentDictionary<int, SemaphoreSlim>();

        private readonly ConcurrentDictionary<int, UserUpdateSubscription> _subscriptions = new ConcurrentDictionary<int, UserUpdateSubscription>();
        private readonly IExchangeUserClientProvider _clientProvider;
        private readonly string[]? _enabledExchanges;

        public UserSubscriptionService(
            ILogger<UserSubscriptionService> logger,
            IConfiguration configuration,
            IExchangeUserClientProvider clientProvider)
        {
            _logger = logger;
            _clientProvider = clientProvider;

            _enabledExchanges = configuration.GetValue<string?>("EnabledExchanges")?.Split(";");
        }

        public async Task<SubscribeResult[]> SubscribeAsync(
            string connectionId,
            int userId,
            IEnumerable<UserExchangeAuthentication> auths,
            Action<ExchangeEvent<SharedBalance[]>> balanceHandler,
            Action<ExchangeEvent<SharedSpotOrder[]>> orderHandler,
            Action<ExchangeEvent<SharedUserTrade[]>> userTradeHandler,
            Action<SubscriptionEvent> statusHandler,
            CancellationToken ct)
        {
            var semaphore = _userSemaphores.GetOrAdd(userId, x => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync();
            try
            {
                _logger.LogInformation("Connecting user subscriptions for user {UserId}", userId);
                _subscriptions.TryGetValue(userId, out var userSub);
                if (userSub != null)
                {
                    // Already a running user subscription, add callback
                    _logger.LogDebug("User subscription for user {UserId} already connected, adding callback. Now {SubCount} subscription for user.", userId, userSub.ConnectionsIds.Count + 1);
                    userSub.AddCallback(new UserCallbacks(connectionId, balanceHandler, orderHandler, userTradeHandler, statusHandler));
                    return [];
                }

                var environments = auths.ToDictionary(x => x.Exchange, x => x.Environment);
                var credentials = new ExchangeCredentials(auths.ToDictionary(x => x.Exchange, x => new ApiCredentials(x.ApiKey, x.ApiSecret, x.ApiPass)));

                var restClient = _clientProvider.GetRestClient(userId.ToString(), credentials, environments);
                var socketClient = _clientProvider.GetSocketClient(userId.ToString(), credentials, environments);

                var cts = new CancellationTokenSource();
                _subscriptions.TryAdd(userId, new UserUpdateSubscription(
                    userId, 
                    socketClient,
                    new UserCallbacks(connectionId, balanceHandler, orderHandler, userTradeHandler, statusHandler),
                    cts));

                var exchanges = auths.Select(x => x.Exchange);
                if (_enabledExchanges?.Any() == true)
                    exchanges = exchanges.Where(x => _enabledExchanges.Contains(x)).ToList();

                var listenKeys = await restClient.StartListenKeysAsync(new StartListenKeyRequest(tradingMode: TradingMode.Spot), exchanges);
                var listenKeyErrors = listenKeys.Where(x => x.Error != null).ToList();

                exchanges = exchanges.Where(x => !listenKeyErrors.Any(y => y.Exchange == x)).ToList();
                var balanceResults = await socketClient.SubscribeToBalanceUpdatesAsync(new SubscribeBalancesRequest(tradingMode: TradingMode.Spot), x => HandleBalanceUpdate(userId, x), exchanges, listenKeys);
                var invalidKeysErrors = balanceResults.Where(x => !x.Success && x.Error!.ErrorType == ErrorType.Unauthorized);
                
                exchanges = exchanges.Where(x => !invalidKeysErrors.Any(y => y.Exchange == x));
                var spotOrderResults = await socketClient.SubscribeToSpotOrderUpdatesAsync(new SubscribeSpotOrderRequest(), x => HandleOrderUpdate(userId, x), exchanges, listenKeys);
                var userTradeResults = await socketClient.SubscribeToUserTradeUpdatesAsync(new SubscribeUserTradeRequest(tradingMode: TradingMode.Spot), x => HandleUserTradeUpdate(userId, x), exchanges, listenKeys);

                if (listenKeys.Where(x => x.Success).Select(x => x.Exchange).Any()) {
                    _ = Task.Run(async () =>
                    {
#warning Mexc exchange has listenkey refresh event, which can't be handled automatically yet

                        while (!cts.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(TimeSpan.FromMinutes(30), cts.Token);
                            }
                            catch { return; }

                            _logger.LogInformation("Sending keep alive listen keys for user {UserId} on exchanges {Exchanges}", userId, listenKeys.Where(x => x.Success).Select(x => x.Exchange));
                            var tasks = new List<Task<ExchangeWebResult<string>>>();
                            foreach (var listenKey in listenKeys.Where(x => x.Success))
                                tasks.Add(restClient.KeepAliveListenKeyAsync(listenKey.Exchange, new KeepAliveListenKeyRequest(listenKey.Data)));
                            await Task.WhenAll(tasks);

                            foreach (var task in tasks.Where(x => !x.Result.Success))
                                _logger.LogWarning("Failed to keep alive listen key for user {UserId} on exchange {Exchange}: {Error}", userId, task.Result.Exchange, task.Result.Error);
                        }
                    });
                }

                foreach (var item in balanceResults.Where(x => x.Success))
                {
                    item.Data.ConnectionRestored += x => ProcessConnectionRestored(userId, item.Exchange);
                    item.Data.ConnectionLost += () => ProcessConnectionLost(userId, item.Exchange);
                }

                foreach (var item in spotOrderResults.Where(x => x.Success))
                {
                    item.Data.ConnectionRestored += x => ProcessConnectionRestored(userId, item.Exchange);
                    item.Data.ConnectionLost += () => ProcessConnectionLost(userId, item.Exchange);
                }

                foreach (var item in userTradeResults.Where(x => x.Success))
                {
                    item.Data.ConnectionRestored += x => ProcessConnectionRestored(userId, item.Exchange);
                    item.Data.ConnectionLost += () => ProcessConnectionLost(userId, item.Exchange);
                }

                var response = new List<SubscribeResult>();
                foreach (var sub in balanceResults)
                    response.Add(new SubscribeResult { Topic = "Balances", Error = sub.Error, Exchange = sub.Exchange });
                foreach (var sub in spotOrderResults)
                    response.Add(new SubscribeResult { Topic = "Orders", Error = sub.Error, Exchange = sub.Exchange });
                foreach (var sub in userTradeResults)
                    response.Add(new SubscribeResult { Topic = "UserTrades", Error = sub.Error, Exchange = sub.Exchange });
                
                foreach (var result in listenKeyErrors)
                {
                    response.Add(new SubscribeResult { Topic = "Balances", Error = result.Error, Exchange = result.Exchange });
                    response.Add(new SubscribeResult { Topic = "Orders", Error = result.Error, Exchange = result.Exchange });
                    response.Add(new SubscribeResult { Topic = "UserTrades", Error = result.Error, Exchange = result.Exchange });
                }

                foreach (var result in invalidKeysErrors)
                {
                    response.Add(new SubscribeResult { Topic = "Orders", Error = result.Error, Exchange = result.Exchange });
                    response.Add(new SubscribeResult { Topic = "UserTrades", Error = result.Error, Exchange = result.Exchange });
                }

                _logger.LogDebug("User subscription for user {UserId} connected", userId);
                return response.ToArray();
            }
            finally
            {
                semaphore.Release();
            }
        }

        private void ProcessConnectionRestored(int userId, string exchange)
        {
            var evnt = new SubscriptionEvent(StreamStatus.Restored) { Exchange = exchange };
            if (_subscriptions.TryGetValue(userId, out var subscription))
                subscription.Invoke(evnt);
        }
        private void ProcessConnectionLost(int userId, string exchange)
        {
            var evnt = new SubscriptionEvent(StreamStatus.Interrupted) { Exchange = exchange };
            if (_subscriptions.TryGetValue(userId, out var subscription))
                subscription.Invoke(evnt);
        }

        private void HandleBalanceUpdate(int userId, ExchangeEvent<SharedBalance[]> update)
        {
            if (_subscriptions.TryGetValue(userId, out var subscription))
                subscription.Invoke(update);
        }

        private void HandleOrderUpdate(int userId, ExchangeEvent<SharedSpotOrder[]> update)
        {
            if (_subscriptions.TryGetValue(userId, out var subscription))
                subscription.Invoke(update);
        }

        private void HandleUserTradeUpdate(int userId, ExchangeEvent<SharedUserTrade[]> update)
        {
            if (_subscriptions.TryGetValue(userId, out var subscription))
                subscription.Invoke(update);
        }


        public async Task UnsubscribeAsync(int userId, string connectionId)
        {
            var semaphore = _userSemaphores[userId];
            await semaphore.WaitAsync();
            try
            {
                if (!_subscriptions.TryGetValue(userId, out var subscription))
                    return;

                if (!subscription.ConnectionsIds.Any(x => x == connectionId))
                    return;

                subscription.Remove(connectionId);
                if (subscription.CallbackCount == 0)
                {
                    _logger.LogDebug("Unsubscribed user subscription for user {UserId}, no listeners, closing connection", subscription.UserId);
                    await subscription.SocketClient.UnsubscribeAllAsync();
                    _subscriptions.Remove(userId, out _);
                    subscription.Cts.Cancel();
                }
                else
                {
                    _logger.LogDebug("Unsubscribed user subscription for user {UserId}, still {Count} listeners left", subscription.UserId, subscription.CallbackCount);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }
    }
}
