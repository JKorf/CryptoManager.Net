using CryptoClients.Net;
using CryptoClients.Net.Enums;
using CryptoClients.Net.Interfaces;
using CryptoClients.Net.Models;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects.Errors;
using CryptoExchange.Net.SharedApis;
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

[ApiController]
[Route("[controller]")]
[ResponseCache(Duration = 10, VaryByQueryKeys = ["*"])]
public class ExchangesController : ApiController
{
    private readonly ILogger _logger;
    private readonly IExchangeUserClientProvider _clientProvider;
    private readonly string[]? _enabledExchanges;

    public ExchangesController(
        ILogger<ExchangesController> logger,
        IConfiguration configuration,
        IExchangeUserClientProvider clientProvider,
        TrackerContext dbContext) : base(dbContext)
    {
        _logger = logger;
        _clientProvider = clientProvider;
        _enabledExchanges = configuration.GetValue<string?>("EnabledExchanges")?.Split(";");
    }

    [HttpGet("names")]
    [ResponseCache(Duration = 600)]
    [ServerCache(Duration = 600)]
    [AllowAnonymous]
    public ApiResult<string[]> GetExchangeNamesAsync()
    {
        return ApiResult<string[]>.Ok(_enabledExchanges ?? Exchange.All);
    }

    [HttpGet("{exchange}/environments")]
    [ResponseCache(Duration = 600, VaryByQueryKeys = ["*"])]
    [ServerCache(Duration = 600)]
    [AllowAnonymous]
    public ApiResult<string[]> GetExchangeEnvironmentsAsync(string exchange)
    {
        var exchangeInfo = Exchanges.All.Single(x => x.Name == exchange);

        return ApiResult<string[]>.Ok(exchangeInfo.ApiEnvironments);
    }

    [HttpGet]
    [ServerCache(Duration = 10)]
    [AllowAnonymous]
    public async Task<ApiResultPaged<IEnumerable<ApiExchange>>> GetExchangesAsync(
        string? query = null,
        string? orderBy = null,
        OrderDirection? orderDirection = null,
        int page = 1,
        int pageSize = 20)
    {
        IQueryable<IGrouping<string, ExchangeSymbol>> dbQuery = _dbContext.Symbols.GroupBy(x => x.Exchange);
        if (_enabledExchanges != null)
            dbQuery = dbQuery.Where(x => _enabledExchanges.Contains(x.Key));

        if (!string.IsNullOrEmpty(query))
            dbQuery = dbQuery.Where(x => x.Key.Contains(query));

        if (string.IsNullOrEmpty(orderBy))
            orderBy = nameof(ExchangeSymbol.UsdVolume);

        var projection = dbQuery.Select(x => new ExchangeProjection
        {
            Exchange = x.Key,
            Symbols = x.Where(x => x.Enabled == true).Count(),
            UsdVolume = x.Sum(x => x.UsdVolume ?? 0)
        });

        Expression<Func<ExchangeProjection, decimal?>> order = orderBy switch
        {
            nameof(ApiExchange.UsdVolume) => exchange => exchange.UsdVolume,
            nameof(ApiExchange.Symbols) => exchange => exchange.Symbols,
            _ => throw new ArgumentException(),
        };

        projection = orderDirection == OrderDirection.Ascending
            ? projection.OrderBy(order)
            : projection.OrderByDescending(order);

        var total = await projection.CountAsync();
        var result = await projection.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return ApiResultPaged<IEnumerable<ApiExchange>>.Ok(page, pageSize, total, result.Select(x => new ApiExchange
        {
            Exchange = x.Exchange,
            UsdVolume = x.UsdVolume,
            Symbols = x.Symbols
        }));
    }

    [HttpGet("{exchange}")]
    [ServerCache(Duration = 10)]
    [AllowAnonymous]
    public async Task<ApiResult<ApiExchangeDetails>> GetExchangeDetailsAsync(string exchange)
    {
        IQueryable<IGrouping<string, ExchangeSymbol>> dbQuery = _dbContext.Symbols.Where(x => x.Exchange == exchange).GroupBy(x => x.Exchange);
        var stats = await dbQuery.Select(x => new ExchangeProjection
        {
            Exchange = x.Key,
            Symbols = x.Where(x => x.Enabled == true).Count(),
            UsdVolume = x.Sum(x => x.UsdVolume ?? 0)
        }).SingleOrDefaultAsync();

        if (stats == null)
            return ApiResult<ApiExchangeDetails>.Error(ErrorType.Unknown, null, "Exchange not found");

        var exchangeInfo = Exchanges.All.Single(x => x.Name == exchange);
        return ApiResult<ApiExchangeDetails>.Ok(new ApiExchangeDetails
        {
            Exchange = exchange,
            Symbols = stats.Symbols,
            UsdVolume = stats.UsdVolume,
            LogoUrl = exchangeInfo.ImageUrl,
            Type = exchangeInfo.Type,
            Url = exchangeInfo.Url
        });
    }

    [HttpGet("{exchange}/fees")]
    [ServerCache(Duration = 60)]
    public async Task<ApiResult<ApiExchangeFees>> GetExchangeFeesAsync(string symbolId, string exchange)
    {
        var apiKeys = await _dbContext.UserApiKeys.Where(x => x.UserId == UserId && !x.Invalid && exchange == null ? true : x.Exchange == exchange).ToListAsync();
        if (!string.IsNullOrEmpty(exchange) && !apiKeys.Any())
            return ApiResult<ApiExchangeFees>.Error(ApiErrors.NoApiKeyConfigured);

        var environments = apiKeys.ToDictionary(x => x.Exchange, x => (string?)x.Environment);
        var credentials = apiKeys.ToDictionary(x => x.Exchange, x => new ApiCredentials(x.Key, x.Secret, x.Pass));
        var client = _clientProvider.GetRestClient(UserId.ToString(), new ExchangeCredentials(credentials), environments);

        var feeClient = client.GetFeeClient(TradingMode.Spot, exchange);
        if (feeClient == null)
            return ApiResult<ApiExchangeFees>.Error(ErrorType.InvalidOperation, null, "Not available");

        var symbolParts = symbolId.Split('-');
        var fees = await feeClient.GetFeesAsync(new GetFeeRequest(new SharedSymbol(TradingMode.Spot, symbolParts[1], symbolParts[2])));
        if (!fees)
            return ApiResult<ApiExchangeFees>.Error(fees.Error!.ErrorType, fees.Error.ErrorCode, fees.Error.Message);

        return ApiResult<ApiExchangeFees>.Ok(new ApiExchangeFees()
        {
            Exchange = exchange,
            MakerFee = fees.Data.MakerFee,
            TakerFee = fees.Data.TakerFee
        });
    }

    private class ExchangeProjection
    {
        public string Exchange { get; set; } = string.Empty;
        public decimal UsdVolume { get; set; }
        public int Symbols { get; set; }
    }
}
