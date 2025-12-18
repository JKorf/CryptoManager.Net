using CryptoClients.Net.Interfaces;
using CryptoClients.Net.Models;
using CryptoExchange.Net;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects.Errors;
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
    [ResponseCache(Duration = 3, Location = ResponseCacheLocation.Client, VaryByQueryKeys = ["*"])]
    [ServerCache(Duration = 3)]
    public class OrdersController : ApiController
    {
        private readonly ILogger _logger;
        private readonly IExchangeUserClientProvider _clientProvider;

        public OrdersController(ILogger<BalancesController> logger, IExchangeUserClientProvider clientProvider, TrackerContext dbContext) : base(dbContext)
        {
            _logger = logger;
            _clientProvider = clientProvider;
        }

        [HttpGet("open")]
        public async Task<ApiResultPaged<IEnumerable<ApiOrder>>> ListOpenAsync(
            string? exchange,
            string? baseAsset,
            string? quoteAsset,
            string? orderBy = null,
            OrderDirection? orderDirection = null,
            int page = 1,
            int pageSize = 20)
        {
            var dbQuery = _dbContext.UserOrders.Where(x => x.UserId == UserId && x.Status == SharedOrderStatus.Open);
            if (!string.IsNullOrEmpty(exchange))
                dbQuery = dbQuery.Where(x => x.Exchange == exchange);

            if (!string.IsNullOrEmpty(baseAsset) && !string.IsNullOrEmpty(quoteAsset))
                dbQuery = dbQuery.Where(x => x.SymbolId.EndsWith($"{baseAsset}-{quoteAsset}"));
            else if (!string.IsNullOrEmpty(baseAsset))
                dbQuery = dbQuery.Where(x => x.SymbolId.Contains($"-{baseAsset}-"));
            else if (!string.IsNullOrEmpty(quoteAsset))
                dbQuery = dbQuery.Where(x => x.SymbolId.EndsWith($"-{quoteAsset}"));

            if (string.IsNullOrEmpty(orderBy))
                orderBy = nameof(ApiOrder.CreateTime);

            Expression<Func<UserOrder, object?>> order = orderBy switch
            {
                nameof(ApiOrder.CreateTime) => order => order.CreateTime,
                nameof(ApiOrder.OrderPrice) => order => order.OrderPrice,
                nameof(ApiOrder.AveragePrice) => order => order.AveragePrice,
                nameof(ApiOrder.OrderQuantityBase) => order => order.OrderQuantityBase,
                nameof(ApiOrder.OrderQuantityQuote) => order => order.OrderQuantityQuote,
                _ => throw new ArgumentException(),
            };

            dbQuery = orderDirection == OrderDirection.Ascending
                ? dbQuery.OrderBy(order)
                : dbQuery.OrderByDescending(order);

            var total = await dbQuery.CountAsync();
            var pageData = await dbQuery.Skip((page - 1) * pageSize).Take(pageSize).AsNoTracking().ToListAsync();

            return ApiResultPaged<IEnumerable<ApiOrder>>.Ok(page, pageSize, total, pageData.Select(x => new ApiOrder
            {
                Exchange = x.Exchange,
                AveragePrice = x.AveragePrice,
                SymbolId = x.SymbolId,
                CreateTime = x.CreateTime,
                Id = x.Id,
                OrderPrice = x.OrderPrice,
                OrderQuantityBase = x.OrderQuantityBase,
                OrderQuantityQuote = x.OrderQuantityQuote,
                OrderSide = x.OrderSide,
                OrderType = x.OrderType,
                QuantityFilledBase = x.QuantityFilledBase,
                QuantityFilledQuote = x.QuantityFilledQuote,
                Status = x.Status
            }));
        }

        [HttpGet("closed")]
        public async Task<ApiResultPaged<IEnumerable<ApiOrder>>> ListClosedAsync(
            string? exchange,
            string baseAsset,
            string quoteAsset,
            string? orderBy = null,
            OrderDirection? orderDirection = null,
            int page = 1,
            int pageSize = 20)
        {
            var partSymbolId = $"-{baseAsset}-{quoteAsset}";
            var dbQuery = _dbContext.UserOrders.Where(x => x.UserId == UserId && (exchange == null ? true : x.Exchange == exchange) && x.SymbolId.EndsWith(partSymbolId) && x.Status != SharedOrderStatus.Open);

            if (string.IsNullOrEmpty(orderBy))
                orderBy = nameof(ApiOrder.CreateTime);

            Expression<Func<UserOrder, object?>> order = orderBy switch
            {
                nameof(ApiOrder.CreateTime) => order => order.CreateTime,
                nameof(ApiOrder.OrderPrice) => order => order.OrderPrice,
                nameof(ApiOrder.AveragePrice) => order => order.AveragePrice,
                nameof(ApiOrder.OrderQuantityBase) => order => order.OrderQuantityBase,
                nameof(ApiOrder.OrderQuantityQuote) => order => order.OrderQuantityQuote,
                _ => throw new ArgumentException(),
            };

            dbQuery = orderDirection == OrderDirection.Ascending
                ? dbQuery.OrderBy(order)
                : dbQuery.OrderByDescending(order);

            var total = await dbQuery.CountAsync();
            var pageData = await dbQuery.Skip((page - 1) * pageSize).Take(pageSize).AsNoTracking().ToListAsync();

            return ApiResultPaged<IEnumerable<ApiOrder>>.Ok(page, pageSize, total, pageData.Select(x => new ApiOrder
            {
                Exchange = x.Exchange,
                AveragePrice = x.AveragePrice,
                SymbolId = x.SymbolId,
                CreateTime = x.CreateTime,
                Id = x.Id,
                OrderPrice = x.OrderPrice,
                OrderQuantityBase = x.OrderQuantityBase,
                OrderQuantityQuote = x.OrderQuantityQuote,
                OrderSide = x.OrderSide,
                OrderType = x.OrderType,
                QuantityFilledBase = x.QuantityFilledBase,
                QuantityFilledQuote = x.QuantityFilledQuote,
                Status = x.Status
            }));
        }

        [HttpPost]
        public async Task<ApiResult> PlaceOrderAsync([FromBody] PlaceOrderRequest request)
        {
            var symbolData = request.SymbolId.Split("-");
            var apiKey = await _dbContext.UserApiKeys.Where(x => x.UserId == UserId && !x.Invalid && x.Exchange == symbolData[0]).SingleOrDefaultAsync();
            if (apiKey == null)
                return ApiResult.Error(ApiErrors.NoApiKeyConfigured);

            var environments = new Dictionary<string, string?>() { { apiKey.Exchange, apiKey.Environment } };
            var credentials = new Dictionary<string, ApiCredentials>() { { apiKey.Exchange, new ApiCredentials(apiKey.Key, apiKey.Secret, apiKey.Pass) } };
            var client = _clientProvider.GetRestClient(UserId.ToString()!, new ExchangeCredentials(credentials), environments);

            var price = request.LimitPrice;
            var orderClient = client.GetSpotOrderClient(symbolData[0]);
            if (price == null && orderClient!.PlaceSpotOrderOptions.RequiredOptionalParameters.Any(x => x.Name == nameof(PlaceSpotOrderRequest.Price)))
            {
                // Some exchanges require a price to be sent even for market orders so they can calculate a slippage and simulate a market order even 
                // if the exchange doesn't directly support it
                var ticker = await _dbContext.Symbols.SingleAsync(x => x.Id == request.SymbolId);
                price = ticker.LastPrice?.Normalize();
            }

            var sharedSymbol = new SharedSymbol(TradingMode.Spot, symbolData[1], symbolData[2]);
            var result = await client.PlaceSpotOrderAsync(symbolData[0],
                new PlaceSpotOrderRequest(
                    sharedSymbol,
                    request.OrderSide,
                    request.OrderType,
                    request.QuoteQuantity > 0 ? SharedQuantity.Quote(request.QuoteQuantity.Value) : SharedQuantity.Base(request.BaseQuantity!.Value),
                    price,
                    request.OrderType == SharedOrderType.LimitMaker ? null : request.TimeInForce));

            if (!result)
                return ApiResult.Error(result.Error!.ErrorType, result.Error.ErrorCode, result.Error.Message);

            return ApiResult.Ok();
        }

        [HttpDelete("{id}")]
        public async Task<ApiResult> CancelOrderAsync(string id)
        {
            var order = await _dbContext.UserOrders.SingleOrDefaultAsync(x => x.UserId == UserId && x.Id == id);
            if (order == null)
            {
                _logger.LogDebug("Order to cancel not found");
                return ApiResult.Error(ErrorType.UnknownOrder, null, "Not found");
            }

            var apiKeys = await _dbContext.UserApiKeys.Where(x => x.UserId == UserId && !x.Invalid && x.Exchange == order.Exchange).ToListAsync();

            var environments = apiKeys.ToDictionary(x => x.Exchange, x => (string?)x.Environment);
            var credentials = apiKeys.ToDictionary(x => x.Exchange, x => new ApiCredentials(x.Key, x.Secret, x.Pass));
            var client = _clientProvider.GetRestClient(UserId.ToString()!, new ExchangeCredentials(credentials), environments);

            var symbolData = order.SymbolId.Split("-");
            var sharedSymbol = new SharedSymbol(TradingMode.Spot, symbolData[1], symbolData[2]);
            var result = await client.CancelSpotOrderAsync(symbolData[0], new CancelOrderRequest(sharedSymbol, order.OrderId));
            if (!result)
                return ApiResult.Error(result.Error!.ErrorType, result.Error!.ErrorCode, result.Error.Message);

            return ApiResult.Ok();
        }

        [HttpGet("{id}")]
        public async Task<ApiResult<ApiOrder>> GetOrderAsync(string id)
        {
            var order = await _dbContext.UserOrders.Where(x => x.UserId == UserId && x.Id == id).SingleOrDefaultAsync();
            if (order == null)
                return ApiResult<ApiOrder>.Error(ErrorType.UnknownOrder, null, "Order not found");

            return ApiResult<ApiOrder>.Ok(new ApiOrder
            {
                Exchange = order.Exchange,
                AveragePrice = order.AveragePrice,
                SymbolId = order.SymbolId,
                CreateTime = order.CreateTime,
                Id = order.Id,
                OrderPrice = order.OrderPrice,
                OrderQuantityBase = order.OrderQuantityBase,
                OrderQuantityQuote = order.OrderQuantityQuote,
                OrderSide = order.OrderSide,
                OrderType = order.OrderType,
                QuantityFilledBase = order.QuantityFilledBase,
                QuantityFilledQuote = order.QuantityFilledQuote,
                Status = order.Status
            });
        }

        [HttpPost("update/open")]
        public async Task<ApiResult> UpdateAsync(string? symbolId = null)
        {
            if (!await CheckUserUpdateTopicAsync(UserUpdateType.OpenOrders))
                return ApiResult.Ok();

            string[]? symbolData = null;
            if (!string.IsNullOrEmpty(symbolId))
                symbolData = symbolId.Split("-");

            var keyQuery = _dbContext.UserApiKeys.Where(x => x.UserId == UserId && !x.Invalid);
            if (symbolData != null)
                keyQuery = keyQuery.Where(x => x.Exchange == symbolData[0]);
            var apiKeys = await keyQuery.ToListAsync();
            if (!apiKeys.Any())
                return ApiResult.Error(ApiErrors.NoApiKeyConfigured);

            var environments = apiKeys.ToDictionary(x => x.Exchange, x => (string?)x.Environment);
            var credentials = apiKeys.ToDictionary(x => x.Exchange, x => new ApiCredentials(x.Key, x.Secret, x.Pass));
            var client = _clientProvider.GetRestClient(UserId.ToString()!, new ExchangeCredentials(credentials), environments);

            ExchangeWebResult<SharedSpotOrder[]>[] orders;
            if (symbolData == null)
            {
                // Filter out any clients for which the symbol parameter is required since we don't have it
                var exchanges = apiKeys.Select(x => x.Exchange).ToList();
                var orderClients = client.GetSpotOrderClients().Where(x => exchanges.Contains(x.Exchange)).ToList();
                foreach (var symbolIdRequiredClient in orderClients.Where(x => x.GetOpenSpotOrdersOptions.RequiredOptionalParameters.Any(x => x.Name == nameof(GetOpenOrdersRequest.Symbol))))
                    exchanges.Remove(symbolIdRequiredClient.Exchange);

                orders = await client.GetSpotOpenOrdersAsync(new GetOpenOrdersRequest(), exchanges);
            }
            else
            {
                orders = await client.GetSpotOpenOrdersAsync(new GetOpenOrdersRequest(new SharedSymbol(TradingMode.Spot, symbolData[1], symbolData[2])), apiKeys.Select(x => x.Exchange));
            }

#warning If open orders doesn't return an order we currently have as open we should close it
            var dbOrders = orders.Where(x => x.Success).SelectMany(x => x.Data.Select(y => ParseOrder(x.Exchange, y))).ToArray();
            await _dbContext.BulkInsertOrUpdateAsync(dbOrders, new BulkConfig { WithHoldlock = false });

#warning Ignore errors here because they're probably because of symbol not existing. Should first check if the symbol exists on the exchange
            //var errors = orders.Where(x => !x).Select(x => new ApiError(x.Error!.ErrorType, x.Error.ErrorCode, x.Exchange + ": " +x.Error.Message));
            //if (errors.Any())
            //    return ApiResult.Error(errors);

            return ApiResult.Ok();
        }

        [HttpPost("update/closed")]
        public async Task<ApiResult> UpdateClosedAsync(string? exchange, string baseAsset, string quoteAsset)
        {
            if (!await CheckUserUpdateTopicAsync(UserUpdateType.ClosedOrders))
                return ApiResult.Ok();

            var apiKeys = await _dbContext.UserApiKeys.Where(x => x.UserId == UserId && !x.Invalid && (exchange == null ? true : x.Exchange == exchange)).ToListAsync();
            if (apiKeys == null)
                return ApiResult.Error(ApiErrors.NoApiKeyConfigured);

            var exchanges = apiKeys.Select(x => x.Exchange);
            var environments = apiKeys.ToDictionary(x => x.Exchange, x => x.Environment);
            var credentials = apiKeys.ToDictionary(x => x.Exchange, x => new ApiCredentials(x.Key, x.Secret, x.Pass));
            var client = _clientProvider.GetRestClient(UserId.ToString()!, new ExchangeCredentials(credentials), environments);

            var symbol = new SharedSymbol(TradingMode.Spot, baseAsset, quoteAsset);
            var closedOrders = await client.GetSpotClosedOrdersAsync(new GetClosedOrdersRequest(symbol), exchanges);

            var dbOrders = closedOrders.Where(x => x.Success).SelectMany(y => y.Data.Select(x => ParseOrder(y.Exchange, x)));
            // Want to update full?
            // Maybe keep a track of until what timestamp a users order history is fully synced so a new update can request up to that point?
            await _dbContext.BulkInsertOrUpdateAsync(dbOrders, new BulkConfig { WithHoldlock = false });

#warning Ignore errors here because they're probably because of symbol not existing. Should first check if the symbol exists on the exchange
            //var failed = closedOrders.FirstOrDefault(x => !x.Success);
            //if (failed != null)
            //    return ApiResult.Error(new ApiError(failed.Error!.ErrorType, failed.Error.ErrorCode, failed.Error.Message));

            return ApiResult.Ok();
        }

        private UserOrder ParseOrder(string exchange, SharedSpotOrder x)
        {
            return new UserOrder
            {
                Id = $"{UserId}-{exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}-{x.OrderId}",
                Exchange = exchange,
                AveragePrice = x.AveragePrice,
                SymbolId = $"{exchange}-{x.SharedSymbol!.BaseAsset}-{x.SharedSymbol!.QuoteAsset}",
                CreateTime = x.CreateTime ?? DateTime.UtcNow,
                UpdateTime = x.UpdateTime ?? DateTime.UtcNow,
                OrderId = x.OrderId,
                OrderPrice = x.OrderPrice,
                OrderQuantityBase = x.OrderQuantity?.QuantityInBaseAsset,
                OrderQuantityQuote = x.OrderQuantity?.QuantityInQuoteAsset,
                OrderSide = x.Side,
                OrderType = x.OrderType,
                QuantityFilledBase = x.QuantityFilled?.QuantityInBaseAsset,
                QuantityFilledQuote = x.QuantityFilled?.QuantityInQuoteAsset,
                Status = x.Status,
                UserId = UserId
            };
        }
    }
}
