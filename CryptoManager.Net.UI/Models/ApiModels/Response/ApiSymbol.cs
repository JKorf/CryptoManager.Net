using CryptoExchange.Net.SharedApis;

namespace CryptoManager.Net.UI.Models.ApiModels.Response
{
    public class ApiSymbol
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public string BaseAsset { get; set; } = string.Empty;
        public string QuoteAsset { get; set; } = string.Empty;
        public decimal? LastPrice { get; set; }
        public decimal Volume { get; set; }
        public decimal? QuoteVolume { get; set; }
        public decimal? UsdVolume { get; set; }
        public decimal? ChangePercentage { get; set; }
        public SharedTickerType TickerType { get; set; }
    }
}
