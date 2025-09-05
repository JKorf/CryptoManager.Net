using MudBlazor;
using System;
using CryptoManager.Net.UI.Authorization;
using CryptoManager.Net.UI.Models;
using CryptoManager.Net.UI.Models.ApiModels.Response;

namespace CryptoManager.Net.UI.Services.Rest
{
    public class SymbolService : RestService
    {
        public SymbolService(AuthStateProvider authStateProvider, HttpClient httpClient, ISnackbar snackbar) 
            : base(authStateProvider, httpClient, snackbar)
        {
        }

        public async Task GetAssetsAsync(
            Func<Page<ApiAsset>, Task> onSuccess,
            Func<ApiError, Task>? onError, 
            string? query = null, 
            string? sort = null, 
            string? sortDirection = null,
            AssetType? assetType = null,
            int? minUsdVolume = null,
            int? page = null,
            int? pageSize = null)
        {
            var parameters = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(query))
                parameters.Add("query", query);
            if (!string.IsNullOrEmpty(sort))
                parameters.Add("orderBy", sort);
            if (!string.IsNullOrEmpty(sortDirection))
                parameters.Add("orderDirection", sortDirection);
            if (assetType != null)
                parameters.Add("assetType", assetType!.ToString()!);
            if (minUsdVolume != null)
                parameters.Add("minUsdVolume", minUsdVolume.ToString()!);
            if (page != null)
                parameters.Add("page", page.ToString()!);
            if (pageSize != null)
                parameters.Add("pageSize", pageSize.ToString()!);

            await GetPageAsync("assets", onSuccess, onError ?? (x => NotifyError("Get Assets", x)), false, parameters);
        }

        public async Task GetSymbolsAsync(
            Func<Page<ApiSymbol>, Task> onSuccess,
            Func<ApiError, Task>? onError,
            string? query = null,
            string? exchange = null,
            string? baseAsset = null,
            string? quoteAsset = null,
            int? minUsdVolume = null,
            string? sort = null,
            string? sortDirection = null,
            int? page = null,
            int? pageSize = null)
        {
            var parameters = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(query))
                parameters.Add("query", query);
            if (!string.IsNullOrEmpty(exchange))
                parameters.Add("exchange", exchange);
            if (!string.IsNullOrEmpty(baseAsset))
                parameters.Add("baseAsset", baseAsset);
            if (!string.IsNullOrEmpty(quoteAsset))
                parameters.Add("quoteAsset", quoteAsset);
            if (minUsdVolume != null)
                parameters.Add("minUsdVolume", minUsdVolume.ToString()!);
            if (!string.IsNullOrEmpty(sort))
                parameters.Add("orderBy", sort);
            if (!string.IsNullOrEmpty(sortDirection))
                parameters.Add("orderDirection", sortDirection);
            if (page != null)
                parameters.Add("page", page.ToString()!);
            if (pageSize != null)
                parameters.Add("pageSize", pageSize.ToString()!);

            await GetPageAsync("symbols", onSuccess, onError ?? (x => NotifyError("Get Symbols", x)), false, parameters);
        }

        public async Task GetSymbolNamesAsync(
            Func<ApiExchangeSymbols, Task> onSuccess,
            Func<ApiError, Task>? onError,
            string exchange)
        {
            var parameters = new Dictionary<string, string>();
            parameters.Add("exchange", exchange);
            await GetAsync("symbols/names", onSuccess, onError ?? (x => NotifyError("Get Symbol Names", x)), false, parameters);
        }

        public async Task GetSymbolDetailsAsync(
            Func<ApiSymbolDetails, Task> onSuccess,
            Func<ApiError, Task>? onError, 
            string symbolId)
        {
            await GetAsync("symbols/" + symbolId, onSuccess, onError ?? (x => NotifyError("Get Symbol Details", x)), false);
        }
    }
}
