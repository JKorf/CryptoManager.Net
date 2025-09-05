using CryptoManager.Net.Database.Models;
using CryptoManager.Net.Database.Projections;
using Microsoft.EntityFrameworkCore;

namespace CryptoManager.Net.Database
{
    public class TrackerContext : DbContext
    {
        public DbSet<ExchangeSymbol> Symbols { get; set; }
        public DbSet<FiatPrice> FiatPrices { get; set; }
        public DbSet<ExchangeAsset> ExchangeAssets { get; set; }
        public DbSet<Asset> Assets { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<UserQuickViewConfiguration> UserQuickViewConfigurations { get; set; }
        public DbSet<RefreshToken> RefreshTokens { get; set; }
        public DbSet<UserApiKey> UserApiKeys { get; set; }
        public DbSet<UserUpdate> UserUpdates { get; set; }
        public DbSet<UserBalance> UserBalances { get; set; }
        public DbSet<UserExternalBalance> UserExternalBalances { get; set; }
        public DbSet<UserOrder> UserOrders { get; set; }
        public DbSet<UserTrade> UserTrades { get; set; }
        public DbSet<UserValuation> UserValuations { get; set; }

        protected TrackerContext()
        {
        }

        public TrackerContext(DbContextOptions<TrackerContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserTrade>().ToTable("UserTrade"); // Temp

            modelBuilder.Entity<ExternalBalanceValue>(entity => { entity.HasNoKey(); entity.ToView(null); });
            modelBuilder.Entity<ExchangeBalanceValue>(entity => { entity.HasNoKey(); entity.ToView(null); });
            modelBuilder.Entity<ExchangeUserValue>(entity => { entity.HasNoKey(); entity.ToView(null); });

            modelBuilder.Entity<ExchangeSymbol>().HasIndex(x => x.UpdateTime).IncludeProperties(x => new { x.Exchange, x.BaseAsset });
            modelBuilder.Entity<ExchangeAsset>().HasIndex(x => x.Exchange).IncludeProperties(x => new { x.Value, x.Asset });
            modelBuilder.Entity<ExchangeAsset>().HasIndex(x => x.AssetType).IncludeProperties(x => new { x.Value, x.Volume, x.ChangePercentage, x.Asset });
        }
    }
}
