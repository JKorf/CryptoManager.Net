using CryptoExchange.Net.Objects.Errors;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.Auth;
using CryptoManager.Net.Data;
using CryptoManager.Net.Database;
using CryptoManager.Net.Database.Models;
using CryptoManager.Net.Models.Response;
using CryptoManager.Net.Subscriptions.OrderBook;
using CryptoManager.Net.Subscriptions.Tickers;
using CryptoManager.Net.Subscriptions.Trades;
using CryptoManager.Net.Subscriptions.User;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoManager.Net.Websockets
{
    // Should be split into connection management and data management for the different topics

    public class WebsocketManager : IHostedService
    {
        private ConcurrentDictionary<string, WebsocketConnection> _connections = new ConcurrentDictionary<string, WebsocketConnection>();

        private readonly ILogger _logger;
        private CancellationTokenSource _cts = new CancellationTokenSource();

        private Task? _checkDisconnectTask;

        private readonly JwtService _jwtService;
        private readonly IDbContextFactory<TrackerContext> _dbContextFactory;

        private readonly TickerSubscriptionService _tickerSubscriptionService;
        private readonly TradeSubscriptionService _tradeSubscriptionService;
        private readonly OrderBookSubscriptionService _orderBookSubscriptionService;
        private readonly UserSubscriptionService _userSubscriptionService;

        private readonly DataBatcher<UserBalance> _balanceBatcher;
        private readonly DataBatcher<UserOrder> _orderBatcher;
        private readonly DataBatcher<UserTrade> _userTradeBatcher;

        private readonly JsonSerializerOptions _serializerOptions;

        public int ConnectionCount => _connections.Count;
        public int UserConnectionCount => _connections.Count;
        public int TickerConnectionCount => _tickerSubscriptionService.ConnectionCount;
        public int TickerSubscriptionCount => _tickerSubscriptionService.SubscriptionCount;
        public int TradeConnectionCount => _tradeSubscriptionService.ConnectionCount;
        public int TradeSubscriptionCount => _tradeSubscriptionService.SubscriptionCount;
        public int OrderBookConnectionCount => _orderBookSubscriptionService.ConnectionCount;
        public int OrderBookSubscriptionCount => _orderBookSubscriptionService.SubscriptionCount;

        public WebsocketManager(
            ILogger<WebsocketManager> logger,
            IDbContextFactory<TrackerContext> dbContextFactory,
            JwtService jwtService,
            TickerSubscriptionService tickerSubscriptionService,
            TradeSubscriptionService tradeSubscriptionService,
            OrderBookSubscriptionService orderBookSubscriptionService,
            UserSubscriptionService userSubscriptionService)
        {
            _logger = logger;
            _dbContextFactory = dbContextFactory;

            _tickerSubscriptionService = tickerSubscriptionService;
            _tradeSubscriptionService = tradeSubscriptionService;
            _orderBookSubscriptionService = orderBookSubscriptionService;
            _userSubscriptionService = userSubscriptionService;

            _jwtService = jwtService;

            _balanceBatcher = new DataBatcher<UserBalance>(TimeSpan.FromSeconds(1), SaveBalancesAsync);
            _orderBatcher = new DataBatcher<UserOrder>(TimeSpan.FromSeconds(0.5), SaveOrdersAsync, (xOld, xUpdate) =>
            {
                xOld.QuantityFilledBase = xUpdate.QuantityFilledBase;
                xOld.QuantityFilledQuote = xUpdate.QuantityFilledQuote;
                xOld.Status = xUpdate.Status;
                xOld.UpdateTime = xUpdate.UpdateTime;
                return xOld;
            });
            _userTradeBatcher = new DataBatcher<UserTrade>(TimeSpan.FromSeconds(0.5), SaveUserTradesAsync);

            _serializerOptions = new JsonSerializerOptions();
            _serializerOptions.Converters.Add(new JsonStringEnumConverter());
        }


        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _balanceBatcher.StartAsync();
            await _orderBatcher.StartAsync();
            await _userTradeBatcher.StartAsync();

            _checkDisconnectTask = CheckDisconnectionsAsync();
        }

        private async Task CheckDisconnectionsAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                var timespan = TimeSpan.FromHours(1);
                var toDisconnect = _connections.Where(x => DateTime.UtcNow - x.Value.ConnectTime > timespan).ToList();
                var tasks = new List<Task>();
                if (toDisconnect.Count > 0)
                    _logger.LogInformation($"Disconnecting {toDisconnect.Count} clients because they're connected longer than {timespan}");

                foreach (var client in toDisconnect)
                {
                    var task = client.Value.Connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "periodic disconnect", CancellationToken.None);
                    tasks.Add(task);
                }

                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception) { }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), _cts.Token);
                }
                catch (Exception) { }
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();

            await _balanceBatcher.StopAsync();
            await _orderBatcher.StopAsync();
            await _userTradeBatcher.StopAsync();

            if (_checkDisconnectTask != null)
                await _checkDisconnectTask;

            var tasks = _connections.Where(x => x.Value.ProcessTask != null).Select(x => x.Value.ProcessTask!).ToArray();
            await Task.WhenAll(tasks);
        }

        public void AddConnection(WebSocket socket, TaskCompletionSource tcs)
        {
            var id = Guid.NewGuid().ToString();
            var registration = new WebsocketConnection(id, socket, tcs);
            _connections.TryAdd(id, registration);
            registration.ProcessTask = ProcessAsync(registration);
            _logger.LogInformation("New websocket client connected, now {Count} clients", _connections.Count);
        }

        private async Task ProcessAsync(WebsocketConnection connection)
        {
            try
            {
                await ReadAsync(connection);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Websocket client unhandled exception during processing");
                await SendResponseAsync(connection.Connection, null, false, "Unknown error");
                return;
            }
            finally
            {
                connection.TaskCompletionSource.SetResult();
                _connections.Remove(connection.Id, out _);

                var tickerTask = _tickerSubscriptionService.UnsubscribeAllAsync(connection.Id);
                var tradeTask = _tradeSubscriptionService.UnsubscribeAllAsync(connection.Id);
                var bookTask = _orderBookSubscriptionService.UnsubscribeAllAsync(connection.Id);
                var userTask = connection.UserId == null ? Task.CompletedTask : _userSubscriptionService.UnsubscribeAsync(connection.UserId.Value, connection.Id);
                await Task.WhenAll(tickerTask, tradeTask, bookTask, userTask);

                _logger.LogInformation("Websocket client disconnected, now {Count} clients", _connections.Count);
            }
        }

        private async Task ReadAsync(WebsocketConnection socket)
        {
            var receiveBuffer = new byte[1024];
            while (!_cts.Token.IsCancellationRequested)
            {
                var receiveResult = await socket.Connection.ReceiveAsync(receiveBuffer, _cts.Token);
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                    break;

                var messageStr = Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.Count);
                var message = JsonSerializer.Deserialize<WebsocketMessage>(messageStr, _serializerOptions)!;
                await HandleMessage(socket, message);
            }
        }

        private async Task HandleMessage(WebsocketConnection connection, WebsocketMessage message)
        {
            if (message.Action == null || !Enum.IsDefined(message.Action.Value))
            {
                await SendResponseAsync(connection.Connection, message.Id, false, "Unknown action");
                return;
            }

            if (message.Action == MessageAction.Authenticate)
            {
                var jwt = message.Jwt;
                if (string.IsNullOrEmpty(jwt))
                {
                    _logger.LogDebug("Authentication request denied, JWT not found");
                    await SendResponseAsync(connection.Connection, message.Id, false, "JWT not provided");
                    return;
                }

                JwtSecurityToken token;
                try
                {
                    token = _jwtService.ValidateToken(jwt);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Authentication request denied, JWT invalid");
                    await SendResponseAsync(connection.Connection, message.Id, false, "JWT validation failed");
                    return;
                }

                var userId = int.Parse(token.Claims.First(x => x.Type == "id").Value);

                if (connection.UserId == userId)
                {
                    // Already authenticated
                    await SendResponseAsync(connection.Connection, message.Id, true);
                    return;
                }

                if (connection.UserId != null)
                {
                    // Trying to authenticate for a different user than currently authenticated..?
                    connection.Connection.Abort();
                    return;
                }

                connection.UserId = userId;

                using var dbContext = _dbContextFactory.CreateDbContext();
                var apiKeys = await dbContext.UserApiKeys.Where(x => x.UserId == userId && !x.Invalid).ToListAsync();
                var auths = apiKeys.Select(x => new UserExchangeAuthentication
                {
                    Exchange = x.Exchange,
                    Environment = x.Environment,
                    ApiKey = x.Key,
                    ApiSecret = x.Secret,
                    ApiPass = x.Pass
                });

                // Try to subscribe user streams but don't wait for it
                _ = Task.Run(async () =>
                {
                    var subResult = await _userSubscriptionService.SubscribeAsync(
                        connection.Id,
                        userId,
                        auths,
                        x => ProcessBalanceUpdate(connection, x),
                        x => ProcessOrderUpdate(connection, x),
                        x => ProcessUserTradeUpdate(connection, x),
                        x => SendStatusUpdate(connection.Connection, "0", x.Status, x.Exchange),
                        _cts.Token);

                    var invalidKeys = subResult.Where(x => !x.Success && (x.Error!.ErrorType == ErrorType.Unauthorized || x.Error!.ErrorType == ErrorType.InvalidParameter)).Select(x => x.Exchange).ToList();
                    using var keyContext = _dbContextFactory.CreateDbContext();
                    var invalidDbKeys = await keyContext.UserApiKeys.Where(x => x.UserId == userId && invalidKeys.Contains(x.Exchange)).ToListAsync();

                    foreach (var key in invalidDbKeys)
                    {
                        _logger.LogDebug("Received unauthorized error for user {UserId} exchange {Exchange}, marking key as invalid", userId, key.Exchange);
                        key.Invalid = true;
                    }

                    await keyContext.SaveChangesAsync();

                    foreach (var result in subResult.Where(x => !x.Success).GroupBy(x => x.Exchange).Select(x => x.First()))
                        SendStatusUpdate(connection.Connection, "0", StreamStatus.Interrupted, result.Exchange, result.Topic + " - " + result.Error!.Message);

                });

                _logger.LogDebug("Connection authenticated for user {User}", userId);
                await SendResponseAsync(connection.Connection, message.Id, true);
                return;
            }


            if (message.SymbolId == null)
            {
                await SendResponseAsync(connection.Connection, message.Id, false, "Missing parameters");
                return;
            }

            if (message.Action == MessageAction.Unsubscribe)
            {
                // Unsub existing subscription
                if (message.Topic == SubscriptionTopic.Ticker)
                {
                    await _tickerSubscriptionService.UnsubscribeAsync(connection.Id, message.SymbolId);
                }
                else if (message.Topic == SubscriptionTopic.Trade)
                {
                    await _tradeSubscriptionService.UnsubscribeAsync(connection.Id, message.SymbolId);
                }
                else if (message.Topic == SubscriptionTopic.OrderBook)
                {
                    await _orderBookSubscriptionService.UnsubscribeAsync(connection.Id, message.SymbolId);
                }
                else
                {
                    await SendResponseAsync(connection.Connection, message.Id, false, "Unknown subscription topic");
                    return;
                }

                await SendResponseAsync(connection.Connection, message.Id, true);
            }
            else if (message.Action == MessageAction.Subscribe)
            {
                // New subscription
                if (message.Topic == SubscriptionTopic.Trade)
                {
                    var subResult = await _tradeSubscriptionService.SubscribeAsync(connection.Id, message.SymbolId, update => SendDataUpdate(connection.Connection, message.Id, update.Data.Select(x => new ApiTrade
                    {
                        Price = x.Price,
                        Quantity = x.Quantity,
                        Side = x.Side,
                        Timestamp = x.Timestamp
                    })),
                        update => SendStatusUpdate(connection.Connection, message.Id, update.Status),
                         _cts.Token);
                    await SendResponseAsync(connection.Connection, message.Id, subResult.Success, subResult.Success ? null : subResult.Error!.ToString());
                }
                else if (message.Topic == SubscriptionTopic.Ticker)
                {
                    var subResult = await _tickerSubscriptionService.SubscribeAsync(connection.Id, message.SymbolId,
                        x => SendDataUpdate(connection.Connection, message.Id, new ApiTicker
                        {
                            SymbolId = $"{x.Exchange}-{x.Data.SharedSymbol!.BaseAsset}-{x.Data.SharedSymbol.QuoteAsset}",
                            ChangePercentage = x.Data.ChangePercentage,
                            HighPrice = x.Data.HighPrice,
                            LastPrice = x.Data.LastPrice,
                            LowPrice = x.Data.LowPrice,
                            QuoteVolume = x.Data.QuoteVolume,
                            Volume = x.Data.Volume,
                        }),
                        update => SendStatusUpdate(connection.Connection, message.Id, update.Status),
                        _cts.Token);
                    await SendResponseAsync(connection.Connection, message.Id, subResult.Success, subResult.Success ? null : subResult.Error!.ToString());
                }
                else if (message.Topic == SubscriptionTopic.OrderBook)
                {
                    var subResult = await _orderBookSubscriptionService.SubscribeAsync(connection.Id, message.SymbolId,
                        update => SendDataUpdate(connection.Connection, message.Id, new ApiBook
                        {
                            Asks = update.Data.Asks,
                            Bids = update.Data.Bids
                        }),
                        update => SendStatusUpdate(connection.Connection, message.Id, update.Status),
                        _cts.Token);
                    await SendResponseAsync(connection.Connection, message.Id, subResult.Success, subResult.Success ? null : subResult.Error!.ToString());
                }
            }
        }

        private void ProcessUserTradeUpdate(WebsocketConnection connection, DataEvent<SharedUserTrade[]> @event)
        {
            _logger.LogDebug("Received user trade update for user {User}, exchange {Exchange}", connection.UserId, @event.Exchange);
            _ = _userTradeBatcher.AddAsync(@event.Data.ToDictionary(x => $"{connection.UserId}-{@event.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}-{x.Id}", x => new UserTrade
            {
                Id = $"{connection.UserId}-{@event.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}-{x.Id}",
                Exchange = @event.Exchange,
                SymbolId = $"{@event.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}",
                OrderId = $"{connection.UserId}-{@event.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}-{x.OrderId}",
                CreateTime = x.Timestamp,
                Fee = x.Fee,
                FeeAsset = x.FeeAsset,
                Price = x.Price,
                Quantity = x.Quantity,
                Role = x.Role,
                TradeId = x.Id,
                UserId = connection.UserId!.Value,
                Side = x.Side
            }));
        }

        private void ProcessOrderUpdate(WebsocketConnection connection, DataEvent<SharedSpotOrder[]> @event)
        {
            _logger.LogDebug("Received order update for user {User}, exchange {Exchange}", connection.UserId, @event.Exchange);
            var trades = @event.Data.Where(x => x.LastTrade != null).Select(y => y.LastTrade!);
            _ = _userTradeBatcher.AddAsync(trades.ToDictionary(x => $"{connection.UserId}-{@event.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}-{x.Id}", x => new UserTrade
            {
                Id = $"{connection.UserId}-{@event.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}-{x.Id}",
                Exchange = @event.Exchange,
                SymbolId = $"{@event.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}",
                OrderId = $"{connection.UserId}-{@event.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}-{x.OrderId}",
                CreateTime = x.Timestamp,
                Fee = x.Fee,
                FeeAsset = x.FeeAsset ?? "-",
                Price = x.Price,
                Quantity = x.Quantity,
                Role = x.Role,
                TradeId = x.Id,
                UserId = connection.UserId!.Value,
                Side = x.Side
            }));

            _ = _orderBatcher.AddAsync(
                @event.Data.GroupBy(x => x.OrderId)
                           .Select(x => x.OrderByDescending(y => y.QuantityFilled?.QuantityInBaseAsset ?? y.QuantityFilled?.QuantityInQuoteAsset).First())
                           .ToDictionary(x => $"{connection.UserId}-{@event.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}-{x.OrderId}", x => new UserOrder
                           {
                               Id = $"{connection.UserId}-{@event.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}-{x.OrderId}",
                               Exchange = @event.Exchange,
                               SymbolId = $"{@event.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}",
                               AveragePrice = x.AveragePrice,
                               OrderId = x.OrderId,
                               Status = x.Status,
                               UpdateTime = x.UpdateTime ?? DateTime.UtcNow,
                               CreateTime = x.CreateTime ?? DateTime.UtcNow,
                               OrderPrice = x.OrderPrice,
                               OrderQuantityBase = x.OrderQuantity?.QuantityInBaseAsset,
                               OrderQuantityQuote = x.OrderQuantity?.QuantityInQuoteAsset,
                               OrderSide = x.Side,
                               OrderType = x.OrderType,
                               QuantityFilledBase = x.QuantityFilled?.QuantityInBaseAsset,
                               QuantityFilledQuote = x.QuantityFilled?.QuantityInQuoteAsset,
                               UserId = connection.UserId!.Value
                           }));
        }

        private void ProcessBalanceUpdate(WebsocketConnection connection, DataEvent<SharedBalance[]> @event)
        {
            _logger.LogDebug("Received balance update for user {User}, exchange {Exchange}", connection.UserId, @event.Exchange);

            _ = _balanceBatcher.AddAsync(@event.Data.ToDictionary(x => $"{connection.UserId}-{@event.Exchange}-{x.Asset}", x => new UserBalance
            {
                Id = $"{connection.UserId}-{@event.Exchange}-{x.Asset}",
                Asset = x.Asset,
                Exchange = @event.Exchange,
                Available = Math.Round(x.Available, 8),
                Total = Math.Round(x.Total, 8),
                UserId = connection.UserId!.Value
            }));
        }

        private async Task SaveBalancesAsync(Dictionary<string, UserBalance> dictionary)
        {
            var context = _dbContextFactory.CreateDbContext();

            // Read the balance from the update from the database to check if they're actually changed
            var readList = dictionary.Values.Select(x => new UserBalance { Id = x.Id }).ToList();
            await context.BulkReadAsync(readList, new BulkConfig { WithHoldlock = false, UpdateByProperties = new List<string> { nameof(UserBalance.Id) } });

            var updatedOrNew = dictionary.Where(x =>
            {
                var balance = readList.SingleOrDefault(r => r.Id == x.Key);
                return balance == null || balance.Available != x.Value.Available || balance.Total != x.Value.Total;
            }).Select(x => x.Value).ToList();
            if (updatedOrNew.Count == 0)
                return;

            _logger.LogDebug("Saving batch of {BatchCount} balances", updatedOrNew.Count);

            try
            {
                await context.BulkInsertOrUpdateAsync(dictionary.Values.ToList(), new BulkConfig { WithHoldlock = false });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process balance update");
                return;
            }

            foreach (var userUpdates in updatedOrNew.GroupBy(x => x.UserId))
            {
                var connectionUpdates = _connections.Where(x => x.Value.UserId == userUpdates.Key).ToList();
                foreach (var connection in connectionUpdates)
                    SendAuthUpdate(connection.Value.Connection, "Balances", userUpdates.Select(x => new ApiBalance { Asset = x.Asset, Available = x.Available, Total = x.Total, Exchange = x.Exchange }));
            }
        }

        private async Task SaveOrdersAsync(Dictionary<string, UserOrder> dictionary)
        {
            _logger.LogDebug("Saving batch of {BatchCount} orders", dictionary.Count);
            var context = _dbContextFactory.CreateDbContext();
            var existingOrders = await context.UserOrders.Where(x => dictionary.Keys.Contains(x.Id)).ToListAsync();

            foreach (var order in dictionary)
            {
                var existingOrder = existingOrders.SingleOrDefault(x => x.Id == order.Key);
                if (existingOrder == null)
                {
                    // New order
                    existingOrders.Add(order.Value);
                    context.UserOrders.Add(order.Value);
                }
                else
                {
                    // Order update
                    if (order.Value.QuantityFilledBase != null)
                        existingOrder.QuantityFilledBase = order.Value.QuantityFilledBase;
                    if (order.Value.QuantityFilledQuote != null)
                        existingOrder.QuantityFilledQuote = order.Value.QuantityFilledQuote;
                    if (order.Value.Status != SharedOrderStatus.Open) // Order status is either already open or it is invalid to put it back to open
                        existingOrder.Status = order.Value.Status;

                    existingOrder.UpdateTime = order.Value.UpdateTime;
                }
            }

            await context.SaveChangesAsync();

            foreach (var userUpdates in dictionary.Values.GroupBy(x => x.UserId))
            {
                var connectionUpdates = _connections.Where(x => x.Value.UserId == userUpdates.Key).ToList();
                foreach (var connection in connectionUpdates)
                {
                    SendAuthUpdate(connection.Value.Connection, "Orders", existingOrders.Select(x => new ApiOrder
                    {
                        Id = x.Id,
                        Exchange = x.Exchange,
                        SymbolId = x.SymbolId,
                        AveragePrice = x.AveragePrice,
                        Status = x.Status,
                        CreateTime = x.CreateTime,
                        OrderPrice = x.OrderPrice,
                        OrderQuantityBase = x.OrderQuantityBase,
                        OrderQuantityQuote = x.OrderQuantityQuote,
                        OrderSide = x.OrderSide,
                        OrderType = x.OrderType,
                        QuantityFilledBase = x.QuantityFilledBase,
                        QuantityFilledQuote = x.QuantityFilledQuote
                    }));
                }
            }
        }

        private async Task SaveUserTradesAsync(Dictionary<string, UserTrade> dictionary)
        {
            _logger.LogDebug("Saving batch of {BatchCount} user trades", dictionary.Count);
            var context = _dbContextFactory.CreateDbContext();
            try
            {
                await context.BulkInsertOrUpdateAsync(dictionary.Values.ToList(), new BulkConfig { WithHoldlock = false });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process user trade update");
                return;
            }

            foreach (var userUpdates in dictionary.Values.GroupBy(x => x.UserId))
            {
                var connectionUpdates = _connections.Where(x => x.Value.UserId == userUpdates.Key).ToList();
                foreach (var connection in connectionUpdates)
                {
                    SendAuthUpdate(connection.Value.Connection, "UserTrades", userUpdates.Select(x => new ApiUserTrade
                    {
                        Id = x.Id,
                        Exchange = x.Exchange,
                        SymbolId = x.SymbolId,
                        Role = x.Role,
                        Quantity = x.Quantity,
                        Price = x.Price,
                        CreateTime = x.CreateTime,
                        OrderSide = x.Side,
                        Fee = x.Fee,
                        FeeAsset = x.FeeAsset
                    }));
                }
            }
        }


        private void SendDataUpdate<T>(WebSocket socket, string id, T data)
        {
            var message = new WebsocketDataUpdate<T> { Id = id, Data = data };
            _ = socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, _serializerOptions)), WebSocketMessageType.Text, true, _cts.Token);
        }

        private void SendStatusUpdate(WebSocket socket, string id, StreamStatus status, string? exchange = null, string? info = null)
        {
            var message = new WebsocketStatusUpdate { Id = id, Status = status, Exchange = exchange, Info = info };
            _ = socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, _serializerOptions)), WebSocketMessageType.Text, true, _cts.Token);
        }

        private void SendAuthUpdate<T>(WebSocket socket, string topic, T data)
        {
            var message = new WebsocketDataUpdate<T> { Id = "0", Topic = topic, Data = data };
            _ = socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, _serializerOptions)), WebSocketMessageType.Text, true, _cts.Token);
        }

        private async Task SendResponseAsync(WebSocket socket, string? id, bool success, string? message = null)
        {
            var data = new WebsocketResponse { Id = id, Success = success, Error = message };
            var strData = JsonSerializer.Serialize(data, _serializerOptions);
            await socket.SendAsync(Encoding.UTF8.GetBytes(strData), WebSocketMessageType.Text, true, _cts.Token);
        }
    }
}
