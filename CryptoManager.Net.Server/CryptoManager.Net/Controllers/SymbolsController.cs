using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.Objects.Errors;
using CryptoManager.Net.Caching;
using CryptoManager.Net.Database;
using CryptoManager.Net.Database.Models;
using CryptoManager.Net.Models.Requests;
using CryptoManager.Net.Models.Response;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace CryptoManager.Net.Controllers;

[Route("[controller]")]
[AllowAnonymous]
[ResponseCache(Duration = 10, VaryByQueryKeys = ["*"])]
public class SymbolsController : ApiController
{
    private readonly ILogger _logger;
    private readonly IExchangeRestClient _restClient;

    public SymbolsController(ILogger<SymbolsController> logger, TrackerContext dbContext, IExchangeRestClient restClient) : base(dbContext)
    {
        _logger = logger;
        _restClient = restClient;
    }

    [HttpGet]
    [ServerCache(Duration = 10)]
    public async Task<ApiResultPaged<IEnumerable<ApiSymbol>>> GetSymbolsAsync(
        string? query = null,
        string? exchange = null,
        string? baseAsset = null,
        string? quoteAsset = null,
        int minUsdVolume = 0,
        string? orderBy = null,
        OrderDirection? orderDirection = null,
        int page = 1,
        int pageSize = 20)
    {
        IQueryable<ExchangeSymbol> dbQuery = _dbContext.Symbols.Where(x => x.UsdVolume >= minUsdVolume);
        if (!string.IsNullOrEmpty(exchange))
            dbQuery = dbQuery.Where(x => x.Exchange == exchange);

        if (!string.IsNullOrEmpty(query))
            dbQuery = dbQuery.Where(x => x.Name.Contains(query));

        if (!string.IsNullOrEmpty(baseAsset))
            dbQuery = dbQuery.Where(x => x.BaseAsset == baseAsset);

        if (!string.IsNullOrEmpty(quoteAsset))
            dbQuery = dbQuery.Where(x => x.QuoteAsset == quoteAsset);
                
        if (string.IsNullOrEmpty(orderBy))
            orderBy = nameof(ExchangeSymbol.UsdVolume);

        Expression<Func<ExchangeSymbol, decimal?>> order = orderBy switch
        {
            nameof(ApiSymbol.Volume) => symbol => symbol.Volume,
            nameof(ApiSymbol.QuoteVolume) => symbol => symbol.QuoteVolume,
            nameof(ApiSymbol.UsdVolume) => symbol => symbol.UsdVolume,
            nameof(ApiSymbol.ChangePercentage) => symbol => symbol.ChangePercentage,
            _ => throw new ArgumentException(),
        };
        
        dbQuery = orderDirection == OrderDirection.Ascending
            ? dbQuery.OrderBy(order)
            : dbQuery.OrderByDescending(order);

        var total = await dbQuery.CountAsync();
        var result = await dbQuery.Skip((page - 1) * pageSize).Take(pageSize).AsNoTracking().ToListAsync();

        return ApiResultPaged<IEnumerable<ApiSymbol>>.Ok(page, pageSize, total, result.Select(x => new ApiSymbol
        {
            Id = x.Id,
            Name = x.BaseAsset + "/" + x.QuoteAsset,
            Exchange = x.Exchange,
            BaseAsset = x.BaseAsset,
            QuoteAsset = x.QuoteAsset,
            LastPrice = x.LastPrice,
            Volume = x.Volume ?? 0,
            QuoteVolume = x.QuoteVolume,
            UsdVolume = x.UsdVolume,
            ChangePercentage = x.ChangePercentage,
            TickerType = x.TickerType
        }));
    }

    [HttpGet("{id}")]
    [ServerCache(Duration = 10)]
    public async Task<ApiResult<ApiSymbolDetails>> GetSymbolAsync(string id)
    {
        var symbol = await _dbContext.Symbols.SingleOrDefaultAsync(x => x.Id == id);
        if (symbol == null)
        {
            _logger.LogDebug("Symbol with id {Id} not found", id);
            return ApiResult<ApiSymbolDetails>.Error(ErrorType.UnknownSymbol, null, "Symbol not found");
        }

        var client = _restClient.GetSpotOrderClient(symbol.Exchange);
        return ApiResult<ApiSymbolDetails>.Ok(new ApiSymbolDetails
        {
            Id = symbol.Id,
            Exchange = symbol.Exchange,
            BaseAsset = symbol.BaseAsset,
            QuoteAsset = symbol.QuoteAsset,
            ChangePercentage = symbol.ChangePercentage,
            LastPrice = symbol.LastPrice,
            Name = symbol.Name,
            QuoteVolume = symbol.QuoteVolume,
            UsdVolume = symbol.UsdVolume,
            Volume = symbol.Volume ?? 0,

            MinNotionalValue = symbol.MinNotionalValue,
            MinTradeQuantity = symbol.MinTradeQuantity,
            PriceDecimals = symbol.PriceDecimals,
            PriceSignificantFigures = symbol.PriceSignificantFigures,
            PriceStep = symbol.PriceStep,
            QuantityDecimals = symbol.QuantityDecimals,
            QuantityStep = symbol.QuantityStep,
            
            SupportPlacement = client?.PlaceSpotOrderOptions.Supported == true,
            FeeAssetType = client?.SpotFeeAssetType,
            FeeDeductionType = client?.SpotFeeDeductionType,
            SupportedTimeInForces = client?.SpotSupportedTimeInForce,
            SupportedOrderTypes = client?.SpotSupportedOrderTypes,
            SupportedQuantities = client?.SpotSupportedOrderQuantity,
        });
    }

    [HttpGet("names")]
    [ResponseCache(Duration = 600, VaryByQueryKeys = ["*"])]
    [ServerCache(Duration = 600)]
    public async Task<ApiResult<ApiExchangeSymbols>> GetSymbolNamesAsync(
        string exchange,
        string? baseAsset = null,
        string? quoteAsset = null)
    {
        var symbolQuery = _dbContext.Symbols.Where(x => x.Exchange == exchange);
        if (!string.IsNullOrEmpty(baseAsset))
            symbolQuery = symbolQuery.Where(x => x.BaseAsset == baseAsset);
        if (!string.IsNullOrEmpty(quoteAsset))
            symbolQuery = symbolQuery.Where(x => x.QuoteAsset == quoteAsset);

        var result = await symbolQuery.GroupBy(x => x.BaseAsset).ToDictionaryAsync(x => x.Key, x => x.Select(y => y.QuoteAsset).ToList());
        return ApiResult<ApiExchangeSymbols>.Ok(new ApiExchangeSymbols { Exchange = exchange, Symbols = result });
    }
}
