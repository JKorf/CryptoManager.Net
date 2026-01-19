using CryptoManager.Net.Database;
using CryptoManager.Net.Publish;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CryptoManager.Net.Processor.FiatPrices
{
    public class FiatPricesProcessService : IBackgroundService
    {
        private CancellationToken _stoppingToken = default;
        private readonly ILogger _logger;
        private readonly IProcessInput<Models.FiatPrice> _processInput;
        private readonly IDbContextFactory<TrackerContext> _dbContextFactory;

        public FiatPricesProcessService(
            ILogger<FiatPricesProcessService> logger,
            IProcessInput<Models.FiatPrice> publishOutput,
            IDbContextFactory<TrackerContext> dbContextFactory)
        {
            _logger = logger;
            _processInput = publishOutput;
            _dbContextFactory = dbContextFactory;
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

        private async Task ProcessAsync(PublishItem<Models.FiatPrice> update)
        {
            var context = _dbContextFactory.CreateDbContext();

            var fiatPrices = new List<Database.Models.FiatPrice>();
            foreach (var item in update.Data)
            {
                var symbol = new Database.Models.FiatPrice
                {
                    Id = item.Currency,
                    Price = item.Price,
                    UpdateTime = DateTime.UtcNow
                };

                fiatPrices.Add(symbol);
            }

            try
            {
                await context.BulkInsertOrUpdateAsync(fiatPrices, new BulkConfig { WithHoldlock = false });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process Fiat prices update");
            }
        }
    }
}
