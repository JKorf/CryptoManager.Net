using CryptoClients.Net.Enums;
using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.Data;
using CryptoManager.Net.Models;
using CryptoManager.Net.Publish;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CryptoManager.Net.Publisher.Tickers
{
    public class TickerPublishService : IBackgroundService
    {
        private CancellationToken _stoppingToken = default;
        private readonly ILogger _logger;
        private readonly IExchangeRestClient _restClient;
        private readonly IExchangeSocketClient _socketClient;
        private readonly IPublishOutput<Ticker> _publishOutput;
        private readonly double _pollInterval;
        private readonly DataBatcher<Ticker> _tickerBatcher;
        private readonly Dictionary<string, SharedSpotSymbol[]> _symbols = new Dictionary<string, SharedSpotSymbol[]>();
        private readonly AsyncResetEvent _symbolsInitialSetEvent = new AsyncResetEvent(false, false);

        private readonly string[]? _enabledExchanges;

        public TickerPublishService(
            ILogger<TickerPublishService> logger,
            IConfiguration configuration,
            IExchangeRestClient restClient,
            IExchangeSocketClient socketClient,
            IPublishOutput<Ticker> publishOutput)
        {
            _logger = logger;
            _restClient = restClient;
            _socketClient = socketClient;
            _publishOutput = publishOutput;

            _pollInterval = configuration.GetValue<double?>("TickersPollInterval") ?? 0.16;
            _enabledExchanges = configuration.GetValue<string?>("EnabledExchanges")?.Split(";");

            _tickerBatcher = new DataBatcher<Ticker>(TimeSpan.FromSeconds(5), PublishTickers);
        }

        private async Task PublishTickers(Dictionary<string, Ticker> dictionary)
        {
            var exchangeData = new PublishItem<Ticker>();
            exchangeData.Data = dictionary.Values;
            await _publishOutput.PublishAsync(exchangeData);
        }

        public async Task ExecuteAsync(CancellationToken ct)
        {
            _stoppingToken = ct;

            _logger.LogDebug("Starting TickerPublishService");

            await _tickerBatcher.StartAsync();

            // Init Symbol Cache
            _ = PollSymbolsAsync();
            await _symbolsInitialSetEvent.WaitAsync();

            // Subscribe websocket which exchange support this
            var subscribedExchanges = await SubscribeTickersAsync(_stoppingToken);
            var pollingExchanges = Exchange.All.Except(subscribedExchanges).ToList();
            if (_enabledExchanges?.Any() == true)
                pollingExchanges = pollingExchanges.Where(x => _enabledExchanges.Contains(x)).ToList();

            _logger.LogInformation("TickerPublishService {SubCount} exchange subscribed, starting polling for {PollCount}", subscribedExchanges.Count, pollingExchanges.Count);

            // For remaining exchanges use polling
            while (!_stoppingToken.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                await PollAsync(pollingExchanges);

                var waitTime = TimeSpan.FromMinutes(_pollInterval);
                waitTime = waitTime.Add(-sw.Elapsed);
                if (waitTime < TimeSpan.FromMilliseconds(1))
                    waitTime = TimeSpan.FromMilliseconds(1);

                try { await Task.Delay(waitTime, _stoppingToken); } catch { }
            }

            await _tickerBatcher.StopAsync();
            _logger.LogDebug("TickerPublishService stopped");
        }

        private async Task PollSymbolsAsync()
        {
            // Poll symbols on interval so we know which ones are no longer support
            while (!_stoppingToken.IsCancellationRequested)
            {
                var results = await _restClient.GetSpotSymbolsAsync(new GetSymbolsRequest(), _enabledExchanges);
                foreach (var result in results.Where(x => x.Success))
                    _symbols[result.Exchange] = result.Data;

                _symbolsInitialSetEvent.Set();
                try { await Task.Delay(TimeSpan.FromMinutes(15), _stoppingToken); } catch { }
            }
        }

        private async Task<List<string>> SubscribeTickersAsync(CancellationToken ct)
        {
            var exchanges = _enabledExchanges ?? Exchange.All;
            var subbedExchanges = new List<string>();
            var allTickerClients = _socketClient.GetTickersClients(TradingMode.Spot).Where(x => exchanges.Contains(x.Exchange));

            foreach(var tickerClient in allTickerClients)
            {
                _logger.LogDebug("TickerPublishService starting all ticker for {Exchange}", tickerClient.Exchange);
                var subResult = await tickerClient.SubscribeToAllTickersUpdatesAsync(new SubscribeAllTickersRequest(), ProcessUpdate, _stoppingToken);
                if (subResult)
                    subbedExchanges.Add(subResult.Exchange);
            }

            var multiTickerClients = _socketClient.GetTickerClients(TradingMode.Spot)
                .Where(x => !subbedExchanges.Contains(x.Exchange) && exchanges.Contains(x.Exchange) && x.SubscribeTickerOptions.SupportsMultipleSymbols);
            foreach (var tickerClient in multiTickerClients)
            {
                if (!_symbols!.TryGetValue(tickerClient.Exchange, out var exchangeSymbols))
                    continue;

                var offset = 0;
                var perPage = tickerClient.SubscribeTickerOptions.MaxSymbolCount ?? exchangeSymbols.Length;
                var pages = Math.Ceiling(exchangeSymbols.Length / (double)perPage);
                if (pages > 10)
                    // Needs more than 10 subs to subscribe to all symbols, just go with ticker
                    continue;

                var exchangeSubs = new List<UpdateSubscription>();
                _logger.LogDebug("TickerPublishService starting batched ticker for {Exchange} in {Pages} batches", tickerClient.Exchange, pages);
                var success = true;
                for (var i = 0; i < pages; i++)
                {
                    var symbols = exchangeSymbols[offset..(offset + perPage)];
                    var subResult = await SubscribeToTickersAsync(tickerClient, symbols);
                    if (!subResult)
                    {
                        await Task.WhenAll(exchangeSubs.Select(x => x.CloseAsync()));
                        success = false;
                        break;
                    }

                    exchangeSubs.Add(subResult.Data);
                }

                if (success)
                    subbedExchanges.Add(tickerClient.Exchange);
            }

            return subbedExchanges;
        }

        private async Task<ExchangeResult<UpdateSubscription>> SubscribeToTickersAsync(ITickerSocketClient tickerClient, SharedSpotSymbol[] symbols)
        {
            var subResult = await tickerClient.SubscribeToTickerUpdatesAsync(new SubscribeTickerRequest(symbols.Select(x => x.SharedSymbol)), ProcessUpdate, _stoppingToken);
            if (!subResult)
            {
                _logger.LogDebug("TickerPublishService batch ticker for {Exchange} failed", tickerClient.Exchange);
                return subResult;
            }

            subResult.Data.ResubscribingFailed += async (x) =>
            {
                if (x.ErrorType == ErrorType.UnknownSymbol
                || x.ErrorType == ErrorType.InvalidParameter)
                {
                    // This probably means the symbol is no longer online
                    // Unsubscribe and resub with the symbols which are correct 
                    _logger.LogError($"TickerPublishService resubscribing failed with error {x.ErrorType}, checking valid symbols and resubscribing");
                    await subResult.Data.CloseAsync();
                    var validSymbols = GetValidSymbols(tickerClient.Exchange, symbols);

#warning if this fails there is no backup to get the tickers up and running again
                    var result = await SubscribeToTickersAsync(tickerClient, validSymbols);
                    if (!result)
                        _logger.LogError($"TickerPublishService resubscribing symbols failed; dropped symbols: [{string.Join(", ", symbols.Select(x => x.Name).Except(validSymbols.Select(x => x.Name)))}]");
                    else
                        _logger.LogError($"TickerPublishService resubscribing symbols succeeded; dropped symbols: [{string.Join(", ", symbols.Select(x => x.Name).Except(validSymbols.Select(x => x.Name)))}]");

                }
            };

            return subResult;
        }

        private SharedSpotSymbol[] GetValidSymbols(string exchange, SharedSpotSymbol[] symbols)
        {
            var exchangeSymbols = _symbols[exchange];
            return symbols.Where(x => exchangeSymbols.Any(y => x.Name == y.Name && x.Trading)).ToArray();
        }

        private void ProcessUpdate(ExchangeEvent<SharedSpotTicker> @event)
        {
            var exchangeData = new PublishItem<Ticker>(@event.Exchange);
            var data = new Dictionary<string, Ticker>();
            if (@event.Data.SharedSymbol == null)
                return;

            data.Add(@event.Exchange + @event.Data.Symbol, ParseTicker(@event.Exchange, @event.Data));
            _ = _tickerBatcher.AddAsync(data);
        }

        private void ProcessUpdate(ExchangeEvent<SharedSpotTicker[]> @event)
        {
            var exchangeData = new PublishItem<Ticker>(@event.Exchange);
            var data = new Dictionary<string, Ticker>();
            foreach (var symbol in @event.Data)
            {
                if (symbol.SharedSymbol == null)
                {
                    continue;
                }

                data.Add(@event.Exchange + symbol.Symbol, ParseTicker(@event.Exchange, symbol));
            }

#warning todo
            _ = _tickerBatcher.AddAsync(data);
        }

        private async Task PollAsync(List<string> exchanges)
        {
            var tickersTasks = _restClient.GetSpotTickersAsyncEnumerable(new GetTickersRequest(), exchanges, _stoppingToken);
            await foreach(var result in tickersTasks)
            {
                if (!result)
                {
                    // TODO someway to publish errors
                    _logger.LogError("Failed to request tickers from exchange {Exchange}: {Error}", result.Exchange, result.Error!.ToString());
                    continue;
                }

                var exchangeData = new PublishItem<Ticker>(result.Exchange);

                var data = new Dictionary<string, Ticker>();
                foreach (var symbol in result.Data)
                {
                    if (symbol.SharedSymbol == null)
                        continue;

                    data.Add(result.Exchange + symbol.Symbol, ParseTicker(result.Exchange, symbol));
                }
                _ = _tickerBatcher.AddAsync(data);
            }
        }

        private Ticker ParseTicker(string exchange, SharedSpotTicker ticker)
        {
            return new Ticker
            {
                Exchange = exchange,
                BaseAsset = ticker.SharedSymbol!.BaseAsset,
                QuoteAsset = ticker.SharedSymbol.QuoteAsset,
                Symbol = ticker.Symbol,
                ChangePercentage = ticker.ChangePercentage,
                HighPrice = ticker.HighPrice,
                LastPrice = ticker.LastPrice,
                LowPrice = ticker.LowPrice,
                Volume = ticker.Volume,
                QuoteVolume = ticker.QuoteVolume
            };
        }
    }
}
