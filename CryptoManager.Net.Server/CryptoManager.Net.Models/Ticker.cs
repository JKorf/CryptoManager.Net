using CryptoExchange.Net.SharedApis;

namespace CryptoManager.Net.Models
{
    /// <summary>
    /// Ticker info
    /// </summary>
    public record Ticker
    {
        /// <summary>
        /// Exchange
        /// </summary>
        public string Exchange { get; set; } = default!;
        /// <summary>
        /// Base asset
        /// </summary>
        public string BaseAsset { get; set; } = default!;
        /// <summary>
        /// Quote asset
        /// </summary>
        public string QuoteAsset { get; set; } = default!;
        /// <summary>
        /// Symbol name
        /// </summary>
        public string Symbol { get; set; } = default!;
        /// <summary>
        /// Last trade price
        /// </summary>
        public decimal? LastPrice { get; set; }
        /// <summary>
        /// Highest price in last 24h
        /// </summary>
        public decimal? HighPrice { get; set; }
        /// <summary>
        /// Lowest price in last 24h
        /// </summary>
        public decimal? LowPrice { get; set; }
        /// <summary>
        /// Trade volume in base asset in the last 24h
        /// </summary>
        public decimal Volume { get; set; }
        /// <summary>
        /// Trade volume in base asset in the last 24h
        /// </summary>
        public decimal? QuoteVolume { get; set; }
        /// <summary>
        /// Change percentage in the last 24h
        /// </summary>
        public decimal? ChangePercentage { get; set; }

        /// <summary>
        /// Ticker type
        /// </summary>
        public SharedTickerType TickerType { get; set; }
    }
}
