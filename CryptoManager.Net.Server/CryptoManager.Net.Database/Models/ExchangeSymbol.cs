using CryptoExchange.Net.SharedApis;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace CryptoManager.Net.Database.Models
{
    [Index(nameof(QuoteAsset))]
    [Index(nameof(Exchange), nameof(BaseAsset))]
    [Index(nameof(BaseAssetExchangeId))]
    public class ExchangeSymbol
    {
        [Key]
        public string Id { get; set; } = string.Empty;
        public string BaseAssetExchangeId { get; set; } = string.Empty;
        public string QuoteAssetExchangeId { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string BaseAsset { get; set; } = string.Empty;
        public string QuoteAsset { get; set; } = string.Empty;
        [Precision(28, 8)]
        public decimal? LastPrice { get; set; }
        [Precision(28, 8)]
        public decimal? HighPrice { get; set; }
        [Precision(28, 8)]
        public decimal? LowPrice { get; set; }
        [Precision(28, 8)]
        public decimal? Volume { get; set; }
        [Precision(28, 8)]
        public decimal? QuoteVolume { get; set; }
        [Precision(28, 2)]
        public decimal? UsdVolume { get; set; }
        [Precision(12, 4)]
        public decimal? ChangePercentage { get; set; }

        [Precision(28, 8)]
        public decimal? MinTradeQuantity { get; set; }
        [Precision(28, 8)]
        public decimal? MinNotionalValue { get; set; }
        [Precision(28, 8)]
        public decimal? QuantityStep { get; set; }
        [Precision(28, 8)]
        public decimal? PriceStep { get; set; }
        public int? QuantityDecimals { get; set; }
        public int? PriceDecimals { get; set; }
        public int? PriceSignificantFigures { get; set; }

        public AssetType QuoteAssetType { get; set; }
        public AssetType BaseAssetType { get; set; }

        public SharedTickerType TickerType { get; set; }
        public bool? Enabled { get; set; }

        public DateTime UpdateTime { get; set; }
        public DateTime? DeleteTime { get; set; }
    }
}
