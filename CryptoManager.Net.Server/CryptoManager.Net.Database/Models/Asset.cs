using CryptoExchange.Net.SharedApis;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace CryptoManager.Net.Database.Models
{
    public class Asset
    {
        [Key]
        public string Id { get; set; } = string.Empty;
        public SharedAssetType AssetType { get; set; }
        public SharedAssetSubType? AssetSubType { get; set; }

        [Precision(28, 8)]
        public decimal? Value { get; set; }

        [Precision(28, 8)]
        public decimal Volume { get; set; }
        [Precision(12, 4)]
        public decimal? ChangePercentage { get; set; }

        public DateTime UpdateTime { get; set; }
    }
}
