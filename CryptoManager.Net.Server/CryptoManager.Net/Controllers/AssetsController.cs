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
public class AssetsController : ApiController
{
    private readonly ILogger _logger;
    private readonly string[]? _enabledExchanges;

    public AssetsController(
        ILogger<AssetsController> logger,
        IConfiguration configuration,
        TrackerContext dbContext) : base(dbContext)
    {
        _logger = logger;
        _enabledExchanges = configuration.GetValue<string?>("EnabledExchanges")?.Split(";");
    }

    [HttpGet]
    [ResponseCache(Duration = 10, VaryByQueryKeys = ["*"])]
    [ServerCache(Duration = 10)]
    public async Task<ApiResultPaged<IEnumerable<ApiAsset>>> GetAssetsAsync(
        string? query = null,
        string? orderBy = null,
        OrderDirection? orderDirection = null,
        AssetType? assetType = null,
        int minUsdVolume = 0,
        int page = 1,
        int pageSize = 20)
    {
        IQueryable<Asset> dbQuery = _dbContext.Assets;

        if (assetType != null)
            dbQuery = dbQuery.Where(x => x.AssetType == assetType.Value);

        if (!string.IsNullOrEmpty(query))
            dbQuery = dbQuery.Where(x => x.Id.Contains(query));

        if (string.IsNullOrEmpty(orderBy))
            orderBy = nameof(ApiAsset.VolumeUsd);

        Expression<Func<Asset, decimal?>> order = orderBy switch
        {
            nameof(ApiAsset.Volume) => asset => asset.Volume,
            nameof(ApiAsset.VolumeUsd) => asset => asset.Volume * asset.Value,
            nameof(ApiAsset.ChangePercentage) => asset => asset.ChangePercentage,
            _ => throw new ArgumentException(),
        };

        dbQuery = orderDirection == OrderDirection.Ascending
            ? dbQuery.OrderBy(order)
            : dbQuery.OrderByDescending(order);

        if (minUsdVolume > 0)
            dbQuery = dbQuery.Where(x => x.Volume * x.Value > minUsdVolume);

        var total = await dbQuery.CountAsync();
        var result = await dbQuery.Skip((page - 1) * pageSize).Take(pageSize).AsNoTracking().ToListAsync();

        var pageResult = ApiResultPaged<IEnumerable<ApiAsset>>.Ok(page, pageSize, total, result.Select(x => new ApiAsset
        {
            Name = x.Id,
            AssetType = x.AssetType,
            Value = x.Value,
            Volume = x.Volume,
            VolumeUsd = x.Value * x.Volume,
            ChangePercentage = x.ChangePercentage
        }));

        return pageResult;
    }
}
