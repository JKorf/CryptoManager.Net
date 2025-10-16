using CryptoClients.Net.Interfaces;
using CryptoClients.Net.Models;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.Caching;
using CryptoManager.Net.Database;
using CryptoManager.Net.Database.Models;
using CryptoManager.Net.Database.Projections;
using CryptoManager.Net.Models.Requests;
using CryptoManager.Net.Models.Response;
using EFCore.BulkExtensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace CryptoManager.Net.Controllers
{
    [ApiController]
    [Route("[controller]")]
    [ResponseCache(Duration = 5, Location = ResponseCacheLocation.Client, VaryByQueryKeys = ["*"])]
    [ServerCache(Duration = 5)]
    public class BalancesController : ApiController
    {
        private readonly ILogger _logger;
        private readonly IExchangeUserClientProvider _clientProvider;

        public BalancesController(
            ILogger<BalancesController> logger, 
            IExchangeUserClientProvider clientProvider,
            TrackerContext dbContext) : base(dbContext) 
        {
            _logger = logger;
            _clientProvider = clientProvider;
        }

        [HttpGet("exchange")]
        public async Task<ApiResultPaged<IEnumerable<ApiBalance>>> ListExchangeAsync(
            string? query = null,
            string? asset = null,
            string? exchange = null,
            string? orderBy = null,
            OrderDirection? orderDirection = null,
            int page = 1,
            int pageSize = 20)
        {
            var dbQuery = _dbContext.UserExchangeBalances(UserId, exchange);
            if (!string.IsNullOrEmpty(query))
                dbQuery = dbQuery.Where(x => x.Asset.Contains(query));

            if (!string.IsNullOrEmpty(asset))
                dbQuery = dbQuery.Where(x => x.Asset == asset);

            if (string.IsNullOrEmpty(orderBy))
                orderBy = nameof(ApiBalance.UsdValue);

            Expression<Func<ExchangeBalanceValue, object?>> order = orderBy switch
            {
                nameof(ApiBalance.Available) => balance => balance.Available,
                nameof(ApiBalance.Total) => balance => balance.Total,
                nameof(ApiBalance.UsdValue) => balance => balance.UsdValue,
                nameof(ApiBalance.Exchange) => balance => balance.Exchange,
                nameof(ApiBalance.Asset) => balance => balance.Asset,
                _ => throw new ArgumentException(),
            };

            dbQuery = orderDirection == OrderDirection.Ascending
                ? dbQuery.OrderBy(order)
                : dbQuery.OrderByDescending(order);

            var total = await dbQuery.CountAsync();
            var x = dbQuery.Skip((page - 1) * pageSize).Take(pageSize).ToQueryString();
            var pageData = await dbQuery.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            return ApiResultPaged<IEnumerable<ApiBalance>>.Ok(page, pageSize, total, pageData.Select(x => new ApiBalance
            {
                Exchange = x.Exchange,
                Asset = x.Asset,
                Available = x.Available,
                Total = x.Total,
                UsdValue = x.UsdValue
            }));
        }

        [HttpGet("external")]
        public async Task<ApiResultPaged<IEnumerable<ApiBalance>>> ListExternalAsync(
            string? query = null,
            string? orderBy = null,
            OrderDirection? orderDirection = null,
            int page = 1,
            int pageSize = 20)
        {
            var dbQuery = _dbContext.UserExternalBalances(UserId);
            if (!string.IsNullOrEmpty(query))
                dbQuery = dbQuery.Where(x => x.Asset.Contains(query));

            if (string.IsNullOrEmpty(orderBy))
                orderBy = nameof(ApiBalance.UsdValue);

            Expression<Func<ExternalBalanceValue, object?>> order = orderBy switch
            {
                nameof(ApiBalance.Total) => balance => balance.Total,
                nameof(ApiBalance.UsdValue) => balance => balance.UsdValue,
                nameof(ApiBalance.Asset) => balance => balance.Asset,
                _ => throw new ArgumentException(),
            };

            dbQuery = orderDirection == OrderDirection.Ascending
                ? dbQuery.OrderBy(order)
                : dbQuery.OrderByDescending(order);

            var total = await dbQuery.CountAsync();
            var x = dbQuery.Skip((page - 1) * pageSize).Take(pageSize).ToQueryString();
            var pageData = await dbQuery.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            return ApiResultPaged<IEnumerable<ApiBalance>>.Ok(page, pageSize, total, pageData.Select(x => new ApiBalance
            {
                Id = x.Id,
                Asset = x.Asset,
                Total = x.Total,
                UsdValue = x.UsdValue
            }));
        }

        [HttpGet("valuation")]
        public async Task<ApiResult<ApiBalanceValuation>> GetValueAsync(string? exchange = null)
        {
            var exchangeValue = await _dbContext.UserExchangeBalances(UserId, exchange).SumAsync(x => x.UsdValue);
            var externalValue = await _dbContext.UserExternalBalances(UserId).SumAsync(x => x.UsdValue);

            return ApiResult<ApiBalanceValuation>.Ok(new ApiBalanceValuation
            {
                Exchange = exchangeValue,
                External = externalValue,
                Total = externalValue + exchangeValue
            });
        }

        [HttpGet("history")]
        public async Task<ApiResult<IEnumerable<ApiUserHistoryItem>>> GetHistoryAsync(string period)
        {
            List<UserValuation> data;
            if (period == "1w")
            {
                // One week, return one point per day
                var compareTime = DateTime.UtcNow.AddDays(-8);
                data = await _dbContext.UserValuations
                    .Where(x => x.UserId == UserId && x.Timestamp > compareTime)
                    .OrderBy(x => x.Timestamp)
                    .GroupBy(x => x.Timestamp.Day)
                    .Select(x => x.First())
                    .ToListAsync();
            }
            else if (period == "1m")
            {
                // One month, return one point per 3 days
                var compareTime = DateTime.UtcNow.AddDays(-31);
                var allData = await _dbContext.UserValuations
                    .Where(x => x.UserId == UserId && x.Timestamp > compareTime)
                    .GroupBy(x => x.Timestamp.Day)
                    .Select(x => x.First())
                    .ToListAsync();

                var orderedData = allData.OrderBy(x => x.Timestamp).ToList();

                data = new List<UserValuation>();
                for(var i = orderedData.Count - 1; i >= 0; i -= 3)                
                    data.Add(orderedData[i]);                
            }
            else
            {
                // One year, return one point per month
                var compareTime = DateTime.UtcNow.AddDays(-366);
                var allData = await _dbContext.UserValuations
                    .Where(x => x.UserId == UserId && x.Timestamp > compareTime)
                    .GroupBy(x => x.Timestamp.Day)
                    .Select(x => x.First())
                    .ToListAsync();

                var lastMonth = -1;
                data = new List<UserValuation>();
                foreach(var item in allData.OrderBy(x => x.Timestamp))
                {
                    if (item.Timestamp.Month != lastMonth)
                    {
                        data.Add(item);
                        lastMonth = item.Timestamp.Month;
                    }
                }
            }

            return ApiResult<IEnumerable<ApiUserHistoryItem>>.Ok(data.OrderBy(x => x.Timestamp).Select(x => new ApiUserHistoryItem
            {
                Value = x.Value,
                Timestamp = new DateTime(x.Timestamp.Year, x.Timestamp.Month, x.Timestamp.Day)
            }));
        }

        [HttpGet("assets")]
        public async Task<ApiResult<IEnumerable<ApiAssetBalance>>> GetUserAssetsAsync(int amount = 10)
        {
            var dataExchange = await _dbContext.UserTotalExchangeAssetBalances(UserId).OrderByDescending(x => x.UsdValue).Take(amount).ToListAsync();
            var dataExternal = await _dbContext.UserExternalBalances(UserId).OrderByDescending(x => x.UsdValue).Take(amount).ToListAsync();

            var result = new List<ApiAssetBalance>();
            foreach (var item in dataExchange)
                result.Add(new ApiAssetBalance { Name = item.Asset, Value = item.UsdValue, Price = Math.Round(item.UsdValue / item.Total, 8) });

            foreach(var item in dataExternal)
            {
                var existing = result.SingleOrDefault(x => x.Name == item.Asset);
                if (existing == null)
                {
                    existing = new ApiAssetBalance { Name = item.Asset, Price = Math.Round(item.UsdValue / item.Total, 8) };
                    result.Add(existing);
                }

                existing.Value += item.UsdValue;
            }

            return ApiResult<IEnumerable<ApiAssetBalance>>.Ok(result.OrderByDescending(x => x.Value).Take(amount));
        }

        [HttpPost("external")]
        public async Task<ApiResult> UpdateExternalBalanceAsync([FromBody] UpdateExternalBalanceRequest request)
        {
            var existingBalance = await _dbContext.UserExternalBalances.SingleOrDefaultAsync(x => x.UserId == UserId && x.Asset == request.Asset);
            if (existingBalance == null)
            {
                existingBalance = new UserExternalBalance()
                {
                    Id = $"{UserId}-{request.Asset}",
                    Asset = request.Asset,
                    UserId = UserId
                };
                _dbContext.UserExternalBalances.Add(existingBalance);
            }

            existingBalance.Total = request.Total;
            await _dbContext.SaveChangesAsync();
            return ApiResult.Ok();
        }

        [HttpDelete("external/{id}")]
        public async Task<ApiResult> DeleteExternalBalanceAsync(string id)
        {
            var existingBalance = await _dbContext.UserExternalBalances.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id);
            if (existingBalance == null)
                return ApiResult.Ok();

            _dbContext.UserExternalBalances.Remove(existingBalance);
            await _dbContext.SaveChangesAsync();
            return ApiResult.Ok();
        }

        [HttpPost("update")]
        public async Task<ApiResult> UpdateAsync(string? exchange)
        {
            if (!await CheckUserUpdateTopicAsync(UserUpdateType.Balances))
                return ApiResult.Ok();

            var apiKeys = await _dbContext.UserApiKeys.Where(x => x.UserId == UserId && !x.Invalid && exchange == null ? true : x.Exchange == exchange).ToListAsync();
            if (!string.IsNullOrEmpty(exchange) && !apiKeys.Any())
                return ApiResult.Error(ApiErrors.NoApiKeyConfigured);

            var environments = apiKeys.ToDictionary(x => x.Exchange, x => (string?)x.Environment);
            var credentials = apiKeys.ToDictionary(x => x.Exchange, x => new ApiCredentials(x.Key, x.Secret, x.Pass));
            var client = _clientProvider.GetRestClient(UserId.ToString(), new ExchangeCredentials(credentials), environments);

            var balanceResults = await client.GetBalancesAsync(new GetBalancesRequest(SharedAccountType.Spot), apiKeys.Select(x => x.Exchange));

            var dbBalances = new List<UserBalance>();
            foreach (var result in balanceResults.Where(x => x.Success))
            {
                dbBalances.AddRange(result.Data.Select(x => new UserBalance
                {
                    Id = $"{UserId}-{result.Exchange}-{x.Asset}",
                    Exchange = result.Exchange,
                    Asset = x.Asset,
                    Available = x.Available,
                    Total = x.Total,
                    UserId = UserId
                }));
            }

            await _dbContext.BulkInsertOrUpdateAsync(dbBalances, new BulkConfig { WithHoldlock = false });
            return ApiResult.Ok();
        }
    }
}
