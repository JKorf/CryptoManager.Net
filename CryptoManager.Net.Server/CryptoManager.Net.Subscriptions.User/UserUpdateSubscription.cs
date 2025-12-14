using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.Data;

namespace CryptoManager.Net.Subscriptions.User
{
    public class UserUpdateSubscription
    {
        private readonly Lock _lock = new Lock();
        private List<UserCallbacks> _callbacks;

        public int UserId { get; set; }
        public IExchangeSocketClient SocketClient { get; set; }
        public CancellationTokenSource Cts { get; set; }
        public List<string> ConnectionsIds
        {
            get
            {
                lock (_lock)
                    return _callbacks.Select(x => x.ConnectionId).ToList();
            }
        }
        public int CallbackCount
        {
            get
            {
                lock (_lock)
                    return _callbacks.Count;
            }
        }


        public UserUpdateSubscription(int userId, IExchangeSocketClient client, UserCallbacks callbacks, CancellationTokenSource cts)
        {
            UserId = userId;
            SocketClient = client;
            Cts = cts;
            _callbacks = [callbacks];
        }

        public void AddCallback(UserCallbacks callback)
        {
            lock (_lock)
            {
                _callbacks.Add(callback);
            }
        }

        public void Remove(string connectionId)
        {
            lock (_lock)
            {
                _callbacks.Remove(_callbacks.Single(x => x.ConnectionId == connectionId));
            }
        }

        public void Invoke(DataEvent<SharedBalance[]> update)
        {
            lock(_lock)
            {
                foreach (var callback in _callbacks)
                    callback.BalanceCallback(update);
            }
        }

        public void Invoke(DataEvent<SharedSpotOrder[]> update)
        {
            lock (_lock)
            {
                foreach (var callback in _callbacks)
                    callback.OrderCallback(update);
            }
        }

        public void Invoke(DataEvent<SharedUserTrade[]> update)
        {
            lock (_lock)
            {
                foreach (var callback in _callbacks)
                    callback.UserTradeCallback(update);
            }
        }

        public void Invoke(SubscriptionEvent update)
        {
            lock (_lock)
            {
                foreach (var callback in _callbacks)
                    callback.StatusCallback(update);
            }
        }
    }

    public class UserCallbacks
    {
        public string ConnectionId { get; set; } = string.Empty;
        public Action<DataEvent<SharedBalance[]>> BalanceCallback { get; set; }
        public Action<DataEvent<SharedSpotOrder[]>> OrderCallback { get; set; }
        public Action<DataEvent<SharedUserTrade[]>> UserTradeCallback { get; set; }
        public Action<SubscriptionEvent> StatusCallback { get; set; }

        public UserCallbacks(string connectionId, 
            Action<DataEvent<SharedBalance[]>> balanceCallback,
            Action<DataEvent<SharedSpotOrder[]>> orderCallback,
            Action<DataEvent<SharedUserTrade[]>> userTradeCallback, 
            Action<SubscriptionEvent> statusCallback)
        {
            ConnectionId = connectionId;
            BalanceCallback = balanceCallback;
            OrderCallback = orderCallback;
            UserTradeCallback = userTradeCallback;
            StatusCallback = statusCallback;
        }
    }
}
