using CryptoExchange.Net.SharedApis;
using Microsoft.EntityFrameworkCore;

namespace CryptoManager.Net.Database.Models
{
    public class UserTrade
    {
        public string Id { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public string SymbolId { get; set; } = string.Empty;
        public string TradeId { get; set; } = string.Empty;
        [Precision(28, 8)]
        public decimal? Price { get; set; }
        [Precision(28, 8)]
        public decimal? Quantity { get; set; }
        public string? FeeAsset { get; set; }
        [Precision(28, 8)]
        public decimal? Fee { get; set; }
        public SharedRole? Role { get; set; }
        public SharedOrderSide? Side { get; set; }
        public DateTime? CreateTime { get; set; }

        public string OrderId { get; set; } = string.Empty;

        public int UserId { get; set; }
        public User User { get; set; } = default!;
    }
}
