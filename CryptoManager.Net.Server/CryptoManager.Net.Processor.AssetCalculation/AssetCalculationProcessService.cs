using CryptoManager.Net.Database;
using CryptoManager.Net.Database.Models;
using CryptoManager.Net.Models;
using CryptoManager.Net.Publish;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CryptoManager.Net.Processor.Tickers
{
    public class AssetCalculationProcessService : IBackgroundService
    {
        private CancellationToken _stoppingToken = default;
        private readonly ILogger _logger;
        private readonly IProcessInput<PendingAssetCalculation> _processInput;
        private readonly IDbContextFactory<TrackerContext> _dbContextFactory;
        private DateTime _lastLog;

        public AssetCalculationProcessService(
            ILogger<AssetCalculationProcessService> logger,
            IProcessInput<PendingAssetCalculation> processInput,
            IDbContextFactory<TrackerContext> dbContextFactory)
        {
            _logger = logger;
            _processInput = processInput;
            _dbContextFactory = dbContextFactory;
        }

        public async Task ExecuteAsync(CancellationToken ct)
        {
            _stoppingToken = ct;

            _logger.LogDebug("Starting AssetCalculationService");
            while (!_stoppingToken.IsCancellationRequested)
            {
                var input = await _processInput.ReadAsync(ct);
                if (input != null)
                    await ProcessAsync(input);
            }

            _logger.LogDebug("AssetCalculationService stopped");
        }

        private async Task ProcessAsync(PublishItem<PendingAssetCalculation> update)
        {
            var sw = Stopwatch.StartNew();
            var context = _dbContextFactory.CreateDbContext();

            try
            {
                var ids = update.Data.Select(x => $"{x.Exchange}-{x.Asset}").ToList();
                var symbolData = await context.Symbols
                                .Where(x => ids.Contains(x.BaseAssetExchangeId))
                                .Select(x => new { x.Exchange, x.BaseAsset, x.BaseAssetType, x.QuoteAsset, x.QuoteAssetType, x.ChangePercentage, x.Volume, x.LastPrice, x.TickerType })
                                .GroupBy(x => new { x.Exchange, x.BaseAsset })
                                .ToListAsync();

                var data = symbolData.Select(x =>
                {
                    var first = x.First();
                    return new ExchangeAsset
                    {
                        Id = $"{first.Exchange}-{first.BaseAsset}",
                        Asset = first.BaseAsset,
                        AssetType = first.BaseAssetType,
                        Exchange = first.Exchange,
                        ChangePercentage = x.Where(x => (x.QuoteAssetType == AssetType.Stable || x.QuoteAsset == "USD") && x.ChangePercentage != null).Average(x => x.ChangePercentage),
                        Value = x.Where(x => (x.QuoteAssetType == AssetType.Stable || x.QuoteAsset == "USD") && x.LastPrice != null).Average(x => x.LastPrice),
                        Volume = x.Sum(x => x.Volume ?? 0),
                        UpdateTime = DateTime.UtcNow,
                        TickerType = first.TickerType
                    };
                });

                await context.BulkInsertOrUpdateAsync(data, new BulkConfig { WithHoldlock = false });
                sw.Stop();

                if (DateTime.UtcNow - _lastLog > TimeSpan.FromMinutes(1))
                {
                    _lastLog = DateTime.UtcNow;
                    _logger.LogInformation($"Exchange asset calculation done in {sw.ElapsedMilliseconds}ms for {update.Data.Count()} items");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in asset calculation");
            }
        }
    }
}
