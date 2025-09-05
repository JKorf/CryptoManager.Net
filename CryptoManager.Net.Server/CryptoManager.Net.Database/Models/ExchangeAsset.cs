using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace CryptoManager.Net.Database.Models
{
    [Index(nameof(Exchange), nameof(Asset))]
    [Index(nameof(Asset), nameof(AssetType))]
    public class ExchangeAsset
    {
        [Key]
        public string Id { get; set; } = string.Empty;
        public string Asset { get; set; } = string.Empty;
        public AssetType AssetType { get; set; }
        public string Exchange { get; set; } = string.Empty;

        [Precision(28, 8)]
        public decimal? Value { get; set; }

        [Precision(28, 8)]
        public decimal Volume { get; set; }
        [Precision(12, 4)]
        public decimal? ChangePercentage { get; set; }

        public DateTime UpdateTime { get; set; }
    }
}
