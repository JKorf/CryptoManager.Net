using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.Database;
using CryptoManager.Net.Database.Models;
using CryptoManager.Net.Publish;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CryptoManager.Net.Analyzers
{
    public class AssetStatsAnalyzerService : IBackgroundService
    {
        private ILogger _logger;
        private readonly IDbContextFactory<TrackerContext> _contextFactory;
        private DateTime _lastLog;

        public AssetStatsAnalyzerService(ILogger<AssetStatsAnalyzerService> logger, IDbContextFactory<TrackerContext> contextFactory)
        {
            _logger = logger;
            _contextFactory = contextFactory;
        }

        public async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await ProcessAsync(ct);
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { }
            }

        }

        private async Task ProcessAsync(CancellationToken ct)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var assetContext = _contextFactory.CreateDbContext();

                // Calculate asset stats over the ExchangeAssets
                var allAssets = await assetContext.ExchangeAssets.Select(x => x.Asset).Distinct().ToArrayAsync();
                var batchSize = 1000;
                var batches = Math.Ceiling(allAssets.Length / (double)batchSize);
                for (var i = 0; i < batches; i++)
                {
                    var updateList = new List<Asset>();

                    var assets = allAssets.Skip(i * batchSize).Take(batchSize).ToArray();
                    using var context = _contextFactory.CreateDbContext();
                    var exchangeAssets = await context.ExchangeAssets.Where(x => assets.Contains(x.Asset)).ToListAsync();
                    var assetsGroups = exchangeAssets.GroupBy(x => x.Asset);
                    foreach (var assetGroup in assetsGroups)
                    {
                        var validExchangeAssets = assetGroup.Where(x => x.Volume > 0 && x.Value > 0).ToList();
                        if (validExchangeAssets.Count == 2)
                        {
                            // Validate the values aren't too far apart
                            var valueDif = validExchangeAssets[0].Value!.Value / validExchangeAssets[1].Value!.Value;
                            if (Math.Abs(1 - valueDif) > 0.20m)
                                // Values more than 20% apart, unclear which is the correct one
                                validExchangeAssets.Clear();
                        }
                        else if (validExchangeAssets.Count > 2)
                        {
                            // Determine what the correct values are
                            var weightedAverageValue = validExchangeAssets.Sum(x => x.Value!.Value * x.Volume) / validExchangeAssets.Sum(x => x.Volume);

                            // Remove all exchange assets which differ more than 20%
                            validExchangeAssets.RemoveAll(x => Math.Abs(1 - (x.Value!.Value / weightedAverageValue)) > 0.2m);
                        }

                        if (validExchangeAssets.Count == 0)
                        {
                            // No valid stats could be determined
                            updateList.Add(new Asset
                            {
                                Id = assetGroup.Key,
                                AssetType = assetGroup.First().AssetType,
                                UpdateTime = DateTime.UtcNow
                            });
                        }
                        else
                        {
                            // For value and volume it doesn't matter which ticker type
                            // but for change percentage we only want to use 24h stats
                            var ticker24H = validExchangeAssets.Where(x => x.TickerType == SharedTickerType.Day24H);
                            var changeList = ticker24H.Any() ? ticker24H : validExchangeAssets;

                            updateList.Add(new Asset
                            {
                                Id = assetGroup.Key,
                                AssetType = assetGroup.First().AssetType,
                                Value = validExchangeAssets.Sum(x => x.Value * x.Volume) / validExchangeAssets.Sum(x => x.Volume),
                                Volume = validExchangeAssets.Sum(x => x.Volume),
                                ChangePercentage = changeList.Sum(x => x.ChangePercentage * x.Volume) / changeList.Sum(x => x.Volume),
                                UpdateTime = DateTime.UtcNow
                            });
                        }
                    }

                    await context.BulkInsertOrUpdateAsync(updateList, new BulkConfig
                    {
                        WithHoldlock = false
                    });
                }

                if (DateTime.UtcNow - _lastLog > TimeSpan.FromMinutes(1))
                {
                    _lastLog = DateTime.UtcNow;
                    _logger.LogInformation($"Asset calculation done in {sw.ElapsedMilliseconds}ms for {allAssets.Length} items");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during AssetStatsAnalyzer calculation");
            }
        }
    }
}
