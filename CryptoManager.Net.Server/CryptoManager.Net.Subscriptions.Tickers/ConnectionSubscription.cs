using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.Data;

namespace CryptoManager.Net.Subscriptions.Tickers
{
    internal class ConnectionSubscription
    {
        public string SymbolId { get; set; }
        public Action<SubscriptionEvent> StatusCallback { get; set; }
        public Action<DataEvent<SharedTicker>> DataCallback { get; set; }

        public ConnectionSubscription(string symbolId, Action<SubscriptionEvent> statusCallback, Action<DataEvent<SharedTicker>> dataCallback)
        {
            SymbolId = symbolId;
            StatusCallback = statusCallback;
            DataCallback = dataCallback;
        }
    }
}
