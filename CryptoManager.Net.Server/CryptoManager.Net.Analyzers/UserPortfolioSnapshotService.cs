using CryptoManager.Net.Database;
using CryptoManager.Net.Database.Models;
using CryptoManager.Net.Publish;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CryptoManager.Net.Analyzers
{
    public class UserPortfolioSnapshotService : IBackgroundService
    {
        private readonly ILogger _logger;
        private readonly IDbContextFactory<TrackerContext> _dbContextFactory;

        public UserPortfolioSnapshotService(ILogger<UserPortfolioSnapshotService> logger, IDbContextFactory<TrackerContext> dbContextFactory)
        {
            _dbContextFactory = dbContextFactory;
            _logger = logger;
        }

        public async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                using (var context = _dbContextFactory.CreateDbContext())
                {
                    try
                    {
                        var allUserExchangeBalances = await context.AllUserExchangeBalances().ToListAsync();
                        var allUserExternalBalances = await context.AllUserExternalBalances().ToListAsync();
                        var timestamp = DateTime.UtcNow;

                        var data = new Dictionary<int, UserValuation>();
                        foreach (var item in allUserExchangeBalances)
                        {
                            data.Add(item.UserId, new UserValuation
                            {
                                Id = Guid.NewGuid().ToString(),
                                UserId = item.UserId,
                                Value = item.UsdValue,
                                Timestamp = timestamp
                            });
                        }

                        foreach (var item in allUserExternalBalances)
                        {
                            if (!data.TryGetValue(item.UserId, out var valuation))
                            {
                                valuation = new UserValuation { Id = Guid.NewGuid().ToString(), UserId = item.UserId, Timestamp = timestamp };
                                data.Add(item.UserId, valuation);
                            }

                            valuation.Value += item.UsdValue;
                        }

                        await context.BulkInsertOrUpdateAsync(data.Values.ToList(), new BulkConfig { WithHoldlock = false });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process user portfolio snapshot");
                        return;
                    }
                }

                try { await Task.Delay(GetWait(), ct); } catch (Exception) { }
            }
        }

        private TimeSpan GetWait()
        {
            var time = DateTime.UtcNow;
            var offset = new DateTime(time.Year, time.Month, time.Day, time.Hour, 0, 0).AddHours(1);
            return offset - time;
        }
    }
}
