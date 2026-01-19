using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.Models;
using CryptoManager.Net.Publish;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CryptoManager.Net.Publisher.Symbols
{
    public class SymbolPublishService : IBackgroundService
    {
        private CancellationToken _stoppingToken = default;
        private readonly ILogger _logger;
        private readonly IExchangeRestClient _restClient;
        private readonly IPublishOutput<Symbol> _publishOutput;
        private readonly int _pollInterval;

        private readonly string[]? _enabledExchanges;

        public SymbolPublishService(
            ILogger<SymbolPublishService> logger,
            IConfiguration configuration,
            IExchangeRestClient restClient,
            IPublishOutput<Symbol> publishOutput)
        {
            _logger = logger;
            _restClient = restClient;
            _publishOutput = publishOutput;

            _pollInterval = configuration.GetValue<int?>("SymbolsPollInterval") ?? 1;
            _enabledExchanges = configuration.GetValue<string?>("EnabledExchanges")?.Split(";");
        }

        public async Task ExecuteAsync(CancellationToken ct)
        {
            _stoppingToken = ct;

            _logger.LogDebug("Starting SymbolService");
            while (!_stoppingToken.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                await ProcessAsync();

                var waitTime = TimeSpan.FromMinutes(_pollInterval);
                waitTime = waitTime.Add(-sw.Elapsed);
                if (waitTime < TimeSpan.FromMilliseconds(1))
                    waitTime = TimeSpan.FromMilliseconds(1);

                try { await Task.Delay(waitTime, _stoppingToken); } catch { }
            }

            _logger.LogDebug("SymbolService stopped");
        }

        private async Task ProcessAsync()
        {
            var symbolsTasks = _restClient.GetSpotSymbolsAsync(new GetSymbolsRequest(), _enabledExchanges, _stoppingToken);
            await Task.WhenAll(symbolsTasks);

            foreach (var result in symbolsTasks.Result)
            {
                if (!result)
                {
                    // TODO someway to publish errors
                    _logger.LogError("Failed to request symbols from exchange {Exchange}: {Error}", result.Exchange, result.Error!.ToString());
                    continue;
                }

                var exchangeData = new PublishItem<Symbol>(result.Exchange);
                var data = new List<Symbol>();
                foreach (var symbol in result.Data.Where(x => x.BaseAsset != x.QuoteAsset)) // Filter some weird symbol response
                {
                    data.Add(new Symbol
                    {
                        BaseAsset = symbol.BaseAsset,
                        QuoteAsset = symbol.QuoteAsset,
                        Name = symbol.Name,
                        MaxTradeQuantity = symbol.MaxTradeQuantity,
                        MinNotionalValue = symbol.MinNotionalValue,
                        QuantityDecimals = symbol.QuantityDecimals,
                        MinTradeQuantity = symbol.MinTradeQuantity,
                        PriceDecimals = symbol.PriceDecimals,
                        PriceSignificantFigures = symbol.PriceSignificantFigures,
                        PriceStep = symbol.PriceStep,
                        QuantityStep = symbol.QuantityStep,
                        Trading = symbol.Trading
                    });
                }

                exchangeData.Data = data;
                await _publishOutput.PublishAsync(exchangeData);
            }
        }
    }
}
