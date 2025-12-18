using CryptoManager.Net.Database;
using CryptoManager.Net.Database.Models;
using CryptoManager.Net.Models;
using CryptoManager.Net.Publish;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoManager.Net.Processor.Symbols
{
    public class SymbolsProcessService : IBackgroundService
    {
        private CancellationToken _stoppingToken = default;
        private readonly ILogger _logger;
        private string[] _usdStableAssets;
        private string[] _fiatAssets;
        private string[] _leveragedTokens;
        private readonly IProcessInput<Symbol> _processInput;
        private readonly IPublishOutput<PendingAssetCalculation> _assetCalcOutput;
        private readonly IDbContextFactory<TrackerContext> _dbContextFactory;

        public SymbolsProcessService(
            IConfiguration configuration,
            ILogger<SymbolsProcessService> logger,
            IProcessInput<Symbol> processInput,
            IPublishOutput<PendingAssetCalculation> assetCalcOutput,
            IDbContextFactory<TrackerContext> dbContextFactory)
        {
            _logger = logger;
            _processInput = processInput;
            _assetCalcOutput = assetCalcOutput;
            _dbContextFactory = dbContextFactory;

            _usdStableAssets = configuration.GetValue<string>("UsdStableAssets")!.Split(";");
            _fiatAssets = configuration.GetValue<string>("FiatAssets")!.Split(";");
            _leveragedTokens = configuration.GetValue<string>("LeveragedTokens")!.Split(";");
        }

        public async Task ExecuteAsync(CancellationToken ct)
        {
            _stoppingToken = ct;

            _logger.LogDebug("Starting SymbolService");
            while (!_stoppingToken.IsCancellationRequested)
            {
                var input = await _processInput.ReadAsync(ct);
                if (input != null)
                    await ProcessAsync(input);
            }

            _logger.LogDebug("SymbolService stopped");
        }

        private async Task ProcessAsync(PublishItem<Symbol> update)
        {
            var context = _dbContextFactory.CreateDbContext();

            // Get the names of all symbols for the exchange currently in the DB so we know which ones are no longer returned
            var exchangeSymbols = await context.Symbols.Where(x => x.Exchange == update.Exchange && x.DeleteTime == null).Select(x => x.Id).ToHashSetAsync();

            var symbols = new List<ExchangeSymbol>();
            foreach (var item in update.Data)
            {
                var id = $"{update.Exchange}-{item.BaseAsset.ToUpperInvariant()}-{item.QuoteAsset.ToUpperInvariant()}";
                var symbol = new ExchangeSymbol
                {
                    Id = id,
                    Exchange = update.Exchange!,
                    BaseAssetExchangeId = $"{update.Exchange}-{item.BaseAsset.ToUpperInvariant()}",
                    QuoteAssetExchangeId = $"{update.Exchange}-{item.QuoteAsset.ToUpperInvariant()}",
                    Name = item.Name,
                    BaseAsset = item.BaseAsset.ToUpperInvariant(),
                    QuoteAsset = item.QuoteAsset.ToUpperInvariant(),
                    MinNotionalValue = item.MinNotionalValue,
                    MinTradeQuantity = item.MinTradeQuantity,
                    PriceDecimals = item.PriceDecimals,
                    PriceSignificantFigures = item.PriceSignificantFigures,
                    QuantityDecimals = item.QuantityDecimals,
                    QuantityStep = item.QuantityStep,
                    BaseAssetType = GetAssetType(item.BaseAsset, true),
                    QuoteAssetType = GetAssetType(item.QuoteAsset, false),
                    Enabled = item.Trading,
                    UpdateTime = DateTime.UtcNow,
                    DeleteTime = null
                };

                symbols.Add(symbol);
                exchangeSymbols.Remove(id);
            }

            // Any symbols no longer returned should be set to deleted in DB
            var symbolsToRemove = new List<ExchangeSymbol>();
            foreach (var item in exchangeSymbols)
            {
                symbolsToRemove.Add(new ExchangeSymbol
                {
                    Id = item,
                    Exchange = update.Exchange!,
                    LastPrice = null,
                    HighPrice = null,
                    LowPrice = null,
                    Volume = 0,
                    QuoteVolume = 0,
                    ChangePercentage = null,
                    Enabled = false,
                    UpdateTime = DateTime.UtcNow,
                    DeleteTime = DateTime.UtcNow
                });
            }

            try
            {
                await context.BulkInsertOrUpdateAsync(symbols, new BulkConfig
                {
                    PropertiesToInclude = [
                        nameof(ExchangeSymbol.Id),
                        nameof(ExchangeSymbol.Exchange),
                        nameof(ExchangeSymbol.Name),
                        nameof(ExchangeSymbol.BaseAsset),
                        nameof(ExchangeSymbol.BaseAssetExchangeId),
                        nameof(ExchangeSymbol.QuoteAsset),
                        nameof(ExchangeSymbol.QuoteAssetExchangeId),
                        nameof(ExchangeSymbol.MinNotionalValue),
                        nameof(ExchangeSymbol.MinTradeQuantity),
                        nameof(ExchangeSymbol.PriceDecimals),
                        nameof(ExchangeSymbol.PriceSignificantFigures),
                        nameof(ExchangeSymbol.QuantityDecimals),
                        nameof(ExchangeSymbol.QuantityStep),
                        nameof(ExchangeSymbol.BaseAssetType),
                        nameof(ExchangeSymbol.QuoteAssetType),
                        nameof(ExchangeSymbol.Enabled),
                        nameof(ExchangeSymbol.UpdateTime),
                        nameof(ExchangeSymbol.DeleteTime)
                    ],
                    WithHoldlock = false
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process updated symbols in Symbol update");
            }

            if (symbolsToRemove.Count > 0)
            {
                try
                {
                    await context.BulkInsertOrUpdateAsync(symbolsToRemove, new BulkConfig
                    {
                        PropertiesToInclude = [
                            nameof(ExchangeSymbol.Id),
                            nameof(ExchangeSymbol.Exchange),
                            nameof(ExchangeSymbol.LastPrice),
                            nameof(ExchangeSymbol.HighPrice),
                            nameof(ExchangeSymbol.LowPrice),
                            nameof(ExchangeSymbol.Volume),
                            nameof(ExchangeSymbol.QuoteVolume),
                            nameof(ExchangeSymbol.ChangePercentage),
                            nameof(ExchangeSymbol.Enabled),
                            nameof(ExchangeSymbol.UpdateTime),
                            nameof(ExchangeSymbol.DeleteTime)
                        ],
                        WithHoldlock = false
                    });

                    // When a symbol is removed the asset stats should be recalculated
                    await _assetCalcOutput.PublishAsync(new PublishItem<PendingAssetCalculation>
                    {
                        Data = symbolsToRemove.Select(x => new PendingAssetCalculation { Exchange = x.Exchange, Asset = x.Id.Split('-')[1] })
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process removed symbols in Symbol update");
                }
            }
        }

        private AssetType GetAssetType(string asset, bool checkLeveraged)
        {
            if (_usdStableAssets.Contains(asset))
                return AssetType.Stable;

            if (_fiatAssets.Contains(asset))
                return AssetType.Fiat;

            if (checkLeveraged && _leveragedTokens.Any(x => asset.Contains(x)))
                return AssetType.LeveragedToken;

            return AssetType.Crypto;
        }
    }
}
