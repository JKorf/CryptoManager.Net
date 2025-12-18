using Microsoft.EntityFrameworkCore;

namespace CryptoManager.Net.Database.Models
{
    public class FiatPrice
    {
        public string Id { get; set; } = string.Empty;
        [Precision(28, 8)]
        public decimal Price { get; set; }
        public DateTime UpdateTime { get; set; }
    }
}
