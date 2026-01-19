using CryptoClients.Net.Interfaces;
using CryptoManager.Net.Models;
using CryptoManager.Net.Publish;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CryptoManager.Net.Publisher.FiatPrices
{
    public class FiatPricePublishService : IBackgroundService
    {
        private CancellationToken _stoppingToken = default;
        private readonly ILogger _logger;
        private readonly IPublishOutput<FiatPrice> _publishOutput;
        private readonly HttpClient _httpClient;
        private readonly string _appId;
        private readonly int _pollInterval;

        public FiatPricePublishService(
            ILogger<FiatPricePublishService> logger,
            IConfiguration configuration,
            IExchangeRestClient restClient,
            IPublishOutput<FiatPrice> publishOutput,
            HttpClient httpClient)
        {
            _logger = logger;
            _publishOutput = publishOutput;
            _httpClient = httpClient;
            _pollInterval = configuration.GetValue<int?>("FiatPricesPollInterval") ?? 1440;
            _appId = configuration["Keys:OpenExchangeRates"] ?? throw new Exception("Missing OpenExchangeRates key");
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
            FiatResponse? result = null;
            try
            {
                // Request prices from https://openexchangerates.org/
                result = await _httpClient.GetFromJsonAsync<FiatResponse>(
                    "https://openexchangerates.org/api/latest.json?" +
                    $"app_id={_appId}" +
                    "&base=USD" +
                    "&prettyprint=false" +
                    "&show_alternative=false");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to request Fiat prices from API");
                return;
            }

            var exchangeData = new PublishItem<FiatPrice>(string.Empty);
            var data = new List<FiatPrice>();
            foreach (var symbol in result!.Rates)
            {
                data.Add(new FiatPrice
                {
                    Currency = symbol.Key,
                    Price = symbol.Value
                });
            }

            exchangeData.Data = data;
            await _publishOutput.PublishAsync(exchangeData);
        }
    }

    internal class FiatResponse
    {
        [JsonPropertyName("rates")]
        public Dictionary<string, decimal> Rates { get; set; } = new Dictionary<string, decimal>();
    }
}
