using CryptoExchange.Net.Objects;
using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.Models.Response;
using CryptoManager.Net.UI.Authorization;
using CryptoManager.Net.UI.Models;
using CryptoManager.Net.UI.Models.ApiModels.Response;
using Microsoft.AspNetCore.Components.Authorization;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoManager.Net.UI.Services.Stream
{
    public class StreamService
    {
        protected readonly string _url;

        private readonly JsonSerializerOptions _options;

        private ClientWebSocket _socket;
        private Task? _process;
        private CancellationTokenSource _cts;
        private ConcurrentDictionary<string, WebsocketSubscription> _subscriptions = new ConcurrentDictionary<string, WebsocketSubscription>();

        private AuthStateProvider _authStateProvider;

        private string? _authRequestId;
        private AsyncResetEvent _authWait = new AsyncResetEvent(false, true);
        private ApiResult? _authResult;

        public bool Connected { get; set; }
        public bool Authenticated { get; set; }

        public event Action? OnStatusChanged;

        public event Func<ApiBalance[], Task>? OnBalanceChanged;
        public event Func<ApiOrder[], Task>? OnOrderChanged;
        public event Func<ApiUserTrade[], Task>? OnUserTradeChanged;
        public event Func<string, string, SubscriptionStatus, Task>? OnAuthSubChanged;

        public StreamService(IConfiguration config, AuthStateProvider authStateProvider)
        {
            _url = config["WSAddress"]!;
            _authStateProvider = authStateProvider;
            _authStateProvider.AuthenticationStateChanged += OnAuthStateChange;
            _authStateProvider.OnAccessTokenRefreshed += OnAccessTokenRefresh;

            _options = new JsonSerializerOptions()
            {
                PropertyNameCaseInsensitive = true
            };
            _options.Converters.Add(new JsonStringEnumConverter());
            _socket = new ClientWebSocket();
            _cts = new CancellationTokenSource();
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _process = ProcessAsync();
        }

        private async void OnAccessTokenRefresh()
        {
            var token = await _authStateProvider.GetAccessTokenAsync();
            if (token != null)
                await AuthenticateAsync(token);
        }

        private async void OnAuthStateChange(Task<AuthenticationState> x)
        {
            var result = await x;
            var token = await _authStateProvider.GetAccessTokenAsync();
            if (token == null)
                TriggerReconnect();
            else
                await AuthenticateAsync(token);
        }

        internal async Task<ApiResult> ConnectAsync(CancellationToken ct)
        {
            _socket = new ClientWebSocket();
            try
            {
                await _socket.ConnectAsync(new Uri($"{_url}/ws"), ct);
                Connected = true;
                OnStatusChanged?.Invoke();

                // Resubscribe
                foreach(var sub in _subscriptions)
                {
                    if (sub.Value.Topic == SubscriptionTopic.Ticker)
                        await SendRequest(sub.Value.Id, MessageAction.Subscribe, SubscriptionTopic.Ticker, sub.Value.SymbolId);
                    if (sub.Value.Topic == SubscriptionTopic.Trade)
                        await SendRequest(sub.Value.Id, MessageAction.Subscribe, SubscriptionTopic.Trade, sub.Value.SymbolId);
                    if (sub.Value.Topic == SubscriptionTopic.OrderBook)
                        await SendRequest(sub.Value.Id, MessageAction.Subscribe, SubscriptionTopic.OrderBook, sub.Value.SymbolId);
                }

                return new ApiResult { Success = true };
            }
            catch (OperationCanceledException)
            { }
            catch (Exception)
            {
                _socket.Abort();
            }

            return new ApiResult { Success = false, Errors = [new ApiError { Message = "Connection failed" }] };
        }

        private async Task ProcessAsync()
        {
            var buffer = new byte[1024 * 1024]; // 1 MB
            var it = 0;
            bool wasConnected = false;

            while (!_cts.IsCancellationRequested) 
            {
                it++;

                if (it != 1)
                {
                    Connected = false;
                    Authenticated = false;
                    OnStatusChanged?.Invoke();
                    if (!wasConnected)
                        // Only wait if this isn't the first retry
                        await Task.Delay(5000);
                }

                var connected = await ConnectAsync(_cts.Token);
                if (!connected.Success)
                {
                    wasConnected = false;
                    continue;
                }

                wasConnected = true;
                var userToken = await _authStateProvider.GetAccessTokenAsync();
                if (userToken != null)
                    _ = AuthenticateAsync(userToken);

                while (_socket.State == WebSocketState.Open)
                {
                    try
                    {
                        var receiveResult = await _socket.ReceiveAsync(buffer, _cts.Token);
                        if (receiveResult.MessageType == WebSocketMessageType.Close)
                        {
                            _socket.Abort();
                            break;
                        }

                        var message = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                        var jsonDoc = JsonDocument.Parse(message);

                        var id = jsonDoc.RootElement.GetProperty("id").GetString()!;
                        if (id == "0")
                        {
                            if (jsonDoc.RootElement.TryGetProperty("status", out var statusProp))
                            {
                                var data = jsonDoc.Deserialize<SubscriptionEvent>(_options)!;
                                // user subscription status update
                                OnAuthSubChanged?.Invoke(data.Exchange, data.Info, data.Status);
                                continue;
                            }

                            // Auth data update
                            var topic = jsonDoc.RootElement.GetProperty("topic").GetString();
                            if (topic == "Balances")
                            {
                                var dataProp = jsonDoc.RootElement.GetProperty("data");
                                var data = dataProp.Deserialize<ApiBalance[]>(_options)!;
                                if (OnBalanceChanged != null)
                                    await OnBalanceChanged.Invoke(data);
                            } 
                            else if (topic == "Orders")
                            {
                                var dataProp = jsonDoc.RootElement.GetProperty("data");
                                var data = dataProp.Deserialize<ApiOrder[]>(_options)!;
                                if (OnOrderChanged != null)
                                    await OnOrderChanged.Invoke(data);
                            }
                            else if (topic == "UserTrades")
                            {
                                var dataProp = jsonDoc.RootElement.GetProperty("data");
                                var data = dataProp.Deserialize<ApiUserTrade[]>(_options)!;
                                if (OnUserTradeChanged != null)
                                    await OnUserTradeChanged.Invoke(data);
                            }
                        }
                        else
                        {
                            if (jsonDoc.RootElement.TryGetProperty("data", out var dataProp))
                            {
                                if (!_subscriptions.TryGetValue(id, out var sub))
                                    continue;

                                if (sub.Topic == SubscriptionTopic.Ticker)
                                {
                                    var tickerSub = (TickerWebsocketSubscription)sub;
                                    var data = dataProp.Deserialize<ApiTicker>(_options)!;
                                    tickerSub.DataCallback(data);
                                } else if (sub.Topic == SubscriptionTopic.Trade)
                                {
                                    var tradeSub = (TradeWebsocketSubscription)sub;
                                    var data = dataProp.Deserialize<ApiTrade[]>(_options)!;
                                    tradeSub.DataCallback(data);
                                } else if (sub.Topic == SubscriptionTopic.OrderBook)
                                {
                                    var bookSub = (OrderBookWebsocketSubscription)sub;
                                    var data = dataProp.Deserialize<ApiBook>(_options)!;
                                    bookSub.DataCallback(data);
                                }
                            }
                            else if (jsonDoc.RootElement.TryGetProperty("status", out var statusProp))
                            {
                                if (!_subscriptions.TryGetValue(id, out var sub))
                                    continue;

                                var data = jsonDoc.Deserialize<SubscriptionEvent>(_options)!;
                                sub.StatusCallback?.Invoke(data);
                            }
                            else
                            {
                                if (_subscriptions.TryGetValue(id, out var sub))
                                {
                                    var subscription = (WebsocketSubscription)sub;
                                    sub.Confirmed = true;
                                    continue;
                                }

                                if (id == _authRequestId)
                                {
                                    var success = jsonDoc.RootElement.GetProperty("success").GetBoolean();
                                    var error = jsonDoc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : (string?)null;
                                    Authenticated = success;
                                    _authResult = new ApiResult { Success = success, Errors = success ? [] : [new ApiError { Message = error! }] };
                                    _authWait.Set();
                                }
                            }
                        }
                    }
                    catch(Exception)
                    {
                        if (_socket.State == WebSocketState.Open)
                            _socket.Abort();
                        break;
                    }
                }
            }
        }

        public async Task DisconnectAsync()
        {
            _cts.Cancel();
            try
            {
                if (_socket.State == WebSocketState.Open)
                    _socket.Abort();
            }
            catch (Exception)
            {
            }

            await (_process ?? Task.CompletedTask);
        }

        public async Task<ApiResult> AuthenticateAsync(string jwt)
        {
            if (Authenticated || _authRequestId != null)
                return new ApiResult { Success = true };

            _authRequestId = Guid.NewGuid().ToString();

            var sendResult = await SendRequest(_authRequestId, MessageAction.Authenticate, jwt: jwt);
            if (!sendResult)
            {
                _authRequestId = null;
                return new ApiResult { Success = false, Errors = [new ApiError { Message = "Failed to send" }]};
            }
            await _authWait.WaitAsync();
            OnStatusChanged?.Invoke();
            var result = _authResult;
            _authResult = null;
            _authRequestId = null;
            return result!;
        }

        public void TriggerReconnect()
        {
            if (_socket.State != WebSocketState.Open)
                return;

            _socket.Abort();
        }

        public async Task<ApiResult> SubscribeToTickerUpdatesAsync(string symbolId, Action<ApiTicker> handler, Action<SubscriptionEvent> statusHandler)
        {
            if (_subscriptions.Any(x => x.Value.SymbolId == symbolId && x.Value.Topic == SubscriptionTopic.Ticker))
                Console.WriteLine($"Subscription again for {SubscriptionTopic.Ticker}, {symbolId}");

            var id = Guid.NewGuid().ToString();
            var result = await SendRequest(id, MessageAction.Subscribe, SubscriptionTopic.Ticker, symbolId);
            if (result)
                _subscriptions.TryAdd(id, new TickerWebsocketSubscription(id, symbolId, handler, statusHandler));

            return new ApiResult { Success = result };
        }

        public Task<ApiResult> UnsubscribeToTickerAsync(string symbolId) => UnsubscribeAsync(SubscriptionTopic.Ticker, symbolId);


        public async Task<ApiResult> SubscribeToTradeUpdatesAsync(string symbolId, Action<ApiTrade[]> handler, Action<SubscriptionEvent> statusHandler)
        {
            if (_subscriptions.Any(x => x.Value.SymbolId == symbolId && x.Value.Topic == SubscriptionTopic.Trade))
                Console.WriteLine($"Subscription again for {SubscriptionTopic.Trade}, {symbolId}");

            var id = Guid.NewGuid().ToString();
            var result = await SendRequest(id, MessageAction.Subscribe, SubscriptionTopic.Trade, symbolId);
            if (result)
                _subscriptions.TryAdd(id, new TradeWebsocketSubscription(id, symbolId, handler, statusHandler));
            return new ApiResult { Success = result };
        }

        public Task<ApiResult> UnsubscribeTradeUpdatesAsync(string symbolId) => UnsubscribeAsync(SubscriptionTopic.Trade, symbolId);


        public async Task<ApiResult> SubscribeToOrderBookUpdatesAsync(string symbolId, Action<ApiBook> handler, Action<SubscriptionEvent> statusHandler)
        {
            if (_subscriptions.Any(x => x.Value.SymbolId == symbolId && x.Value.Topic == SubscriptionTopic.OrderBook))
                Console.WriteLine($"Subscription again for {SubscriptionTopic.OrderBook}, {symbolId}");

            var id = Guid.NewGuid().ToString();
            var result = await SendRequest(id, MessageAction.Subscribe, SubscriptionTopic.OrderBook, symbolId);
            if (result)
                _subscriptions.TryAdd(id, new OrderBookWebsocketSubscription(id, symbolId, handler, statusHandler));
            return new ApiResult { Success = result };
        }

        public Task<ApiResult> UnsubscribeToOrderBookAsync(string symbolId) => UnsubscribeAsync(SubscriptionTopic.OrderBook, symbolId);

        private async Task<ApiResult> UnsubscribeAsync(SubscriptionTopic topic, string symbolId)
        {
            var subs = _subscriptions.Values.Where(x => x.Topic == topic && x.SymbolId == symbolId);
            if (!subs.Any())
                return new ApiResult() { Success = true };

            if (subs.Count() > 1)
            {
                // Shouldn't happen;
                Console.WriteLine($"Multiple subscription found for {topic}, {symbolId}");
            }

            var sub = subs.Single();
            _subscriptions.TryRemove(sub.Id, out _);

            var id = Guid.NewGuid().ToString();
            var result = await SendRequest(id, MessageAction.Unsubscribe, topic, symbolId);
            return new ApiResult { Success = result };
        }


        private async Task<bool> SendRequest(string id, MessageAction action, SubscriptionTopic? topic = null, string? symbolId = null, string? jwt = null)
        {
            var data = JsonSerializer.Serialize(new WebsocketMessage
            {
                Id = id,
                Action = action,
                Topic = topic,
                SymbolId = symbolId,
                Jwt = jwt
            });

            try
            {
                await _socket.SendAsync(Encoding.UTF8.GetBytes(data), WebSocketMessageType.Text, true, default);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
