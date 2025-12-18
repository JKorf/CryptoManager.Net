using CryptoExchange.Net.Objects.Errors;
using CryptoManager.Net.Database;
using CryptoManager.Net.Database.Models;
using CryptoManager.Net.Models.Response;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoManager.Net.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class QuickViewController : ApiController
    {
        private readonly ILogger _logger;
        private int _maxSymbols;

        public QuickViewController(ILogger<QuickViewController> logger, IConfiguration config, TrackerContext dbContext) : base(dbContext)
        {
            _logger = logger;
            _maxSymbols = config.GetValue<int?>("MaxQuickViewSymbols") ?? 8;
        }

        [HttpGet]
        public async Task<ApiResult<IEnumerable<string>>> ListAsync()
        {
            var result = await _dbContext.UserQuickViewConfigurations.Where(x => x.UserId == UserId).AsNoTracking().ToListAsync();
            return ApiResult<IEnumerable<string>>.Ok(result.Select(x => x.SymbolId));
        }

        [HttpPost]
        public async Task<ApiResult> AddSymbolAsync([FromBody] ApiQuickViewConfig request)
        {
            var currentCount = await _dbContext.UserQuickViewConfigurations.Where(x => x.UserId == UserId).CountAsync();
            if (currentCount >= _maxSymbols)
                return ApiResult.Error(ErrorType.Unknown, null, "Too many symbols");

            _dbContext.Add(new UserQuickViewConfiguration
            {
                SymbolId = request.Symbol,
                UserId = UserId
            });

            await _dbContext.SaveChangesAsync();

            return ApiResult.Ok();
        }


        [HttpDelete("{symbolId}")]
        public async Task<ApiResult> RemoveSymbolAsync(string symbolId)
        {
            var config = await _dbContext.UserQuickViewConfigurations.SingleOrDefaultAsync(x => x.UserId == UserId && x.SymbolId == symbolId);
            if (config == null)
                return ApiResult.Ok();

            _dbContext.UserQuickViewConfigurations.Remove(config);
            await _dbContext.SaveChangesAsync();

            return ApiResult.Ok();
        }
    }
}
