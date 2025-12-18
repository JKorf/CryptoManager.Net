using CryptoClients.Net.Interfaces;
using CryptoClients.Net.Models;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.Caching;
using CryptoManager.Net.Database;
using CryptoManager.Net.Database.Models;
using CryptoManager.Net.Models.Requests;
using CryptoManager.Net.Models.Response;
using EFCore.BulkExtensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace CryptoManager.Net.Controllers
{
    [Route("[controller]")]
    public class TradesController : ApiController
    {
        private readonly ILogger _logger;
        private readonly IExchangeUserClientProvider _clientProvider;

        public TradesController(ILogger<TradesController> logger, IExchangeUserClientProvider clientProvider, TrackerContext dbContext)
            : base(dbContext)
        {
            _logger = logger;
            _clientProvider = clientProvider;
        }

        [HttpGet]
        [ResponseCache(Duration = 3, Location = ResponseCacheLocation.Client, VaryByQueryKeys = ["*"])]
        [ServerCache(Duration = 3)]
        public async Task<ApiResultPaged<IEnumerable<ApiUserTrade>>> ListAsync(
            string? symbolId = null,
            string? orderId = null,
            string? orderBy = null,
            OrderDirection? orderDirection = null,
            int page = 1,
            int pageSize = 20)
        {
            var dbQuery = _dbContext.UserTrades.Where(x => x.UserId == UserId);
            if (!string.IsNullOrEmpty(symbolId))
                dbQuery = dbQuery.Where(x => x.SymbolId == symbolId);

            if (!string.IsNullOrEmpty(orderId))
                dbQuery = dbQuery.Where(x => x.OrderId == orderId);

            if (string.IsNullOrEmpty(orderBy))
                orderBy = nameof(ApiOrder.CreateTime);

            Expression<Func<UserTrade, object?>> order = orderBy switch
            {
                nameof(UserTrade.CreateTime) => trade => trade.CreateTime,
                _ => throw new ArgumentException(),
            };

            dbQuery = orderDirection == OrderDirection.Ascending
                ? dbQuery.OrderBy(order)
                : dbQuery.OrderByDescending(order);

            var total = await dbQuery.CountAsync();
            var pageData = await dbQuery.Skip((page - 1) * pageSize).Take(pageSize).AsNoTracking().ToListAsync();

            return ApiResultPaged<IEnumerable<ApiUserTrade>>.Ok(page, pageSize, total, pageData.Select(x => new ApiUserTrade
            {
                Exchange = x.Exchange,
                SymbolId = x.SymbolId,
                CreateTime = x.CreateTime,
                OrderSide = x.Side,
                Id = x.Id,
                Fee = x.Fee,
                FeeAsset = x.FeeAsset,
                Price = x.Price,
                Quantity = x.Quantity,
                Role = x.Role
            }));
        }

        [HttpPost("update")]
        public async Task<ApiResult> UpdateAsync(string symbolId, string? orderId = null)
        {
            if (!await CheckUserUpdateTopicAsync(UserUpdateType.ClosedOrders))
                return ApiResult.Ok();

            var symbolData = symbolId.Split("-");
            var apiKey = await _dbContext.UserApiKeys.Where(x => x.UserId == UserId && !x.Invalid && x.Exchange == symbolData[0]).SingleOrDefaultAsync();
            if (apiKey == null)
                return ApiResult.Error(ApiErrors.NoApiKeyConfigured);

            var environments = new Dictionary<string, string?>() { { apiKey.Exchange, apiKey.Environment } };
            var credentials = new Dictionary<string, ApiCredentials>() { { apiKey.Exchange, new ApiCredentials(apiKey.Key, apiKey.Secret, apiKey.Pass) } };
            var client = _clientProvider.GetRestClient(UserId.ToString(), new ExchangeCredentials(credentials), environments);

            ExchangeWebResult<SharedUserTrade[]> userTrades;
            if (!string.IsNullOrEmpty(orderId))
            {
                var orderIdData = orderId.Split("-");
                userTrades = await client.GetSpotOrderTradesAsync(symbolData[0], new GetOrderTradesRequest(new SharedSymbol(TradingMode.Spot, symbolData[1], symbolData[2]), orderIdData[4]));

            }
            else
            {
                userTrades = await client.GetSpotUserTradesAsync(symbolData[0], new GetUserTradesRequest(new SharedSymbol(TradingMode.Spot, symbolData[1], symbolData[2])));
            }

            if (!userTrades)
                return ApiResult.Error(userTrades.Error!.ErrorType, userTrades.Error.ErrorCode, userTrades.Error.Message);

            var dbTrades = userTrades.Data.Select(x => new UserTrade
            {
                Id = $"{UserId}-{userTrades.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}-{x.Id}",
                UserId = UserId,
                OrderId = $"{UserId}-{userTrades.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}-{x.OrderId}",
                SymbolId = $"{userTrades.Exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}",
                CreateTime = x.Timestamp,
                Exchange = userTrades.Exchange,
                Fee = x.Fee,
                FeeAsset = x.FeeAsset,
                Price = x.Price,
                Quantity = x.Quantity,
                Role = x.Role,
                TradeId = x.Id,
                Side = x.Side
            });

            await _dbContext.BulkInsertOrUpdateAsync(dbTrades, new BulkConfig { WithHoldlock = false });
            return ApiResult.Ok();
        }

    }
}
