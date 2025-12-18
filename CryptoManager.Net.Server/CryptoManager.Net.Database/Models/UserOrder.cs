using CryptoExchange.Net.SharedApis;
using Microsoft.EntityFrameworkCore;

namespace CryptoManager.Net.Database.Models
{
    public class UserOrder
    {
        public string Id { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public string SymbolId { get; set; } = string.Empty;
        public string OrderId { get; set; } = string.Empty;
        public SharedOrderSide OrderSide { get; set; }
        public SharedOrderType OrderType { get; set; }
        public SharedOrderStatus Status { get; set; }
        [Precision(28, 8)]
        public decimal? OrderPrice { get; set; }
        [Precision(28, 8)]
        public decimal? OrderQuantityBase { get; set; }
        [Precision(28, 8)]
        public decimal? OrderQuantityQuote { get; set; }
        [Precision(28, 8)]
        public decimal? QuantityFilledBase { get; set; }
        [Precision(28, 8)]
        public decimal? QuantityFilledQuote { get; set; }
        [Precision(28, 8)]
        public decimal? AveragePrice { get; set; }
        public DateTime CreateTime { get; set; }
        public DateTime UpdateTime { get; set; }
        public int UserId { get; set; }
        public virtual User User { get; set; } = default!;
    }
}
