using MudBlazor;
using CryptoManager.Net.UI.Authorization;
using CryptoManager.Net.UI.Models;
using CryptoManager.Net.UI.Services.Rest;
using CryptoManager.Net.UI.Models.ApiModels.Response;

namespace CryptoManager.Net.UI.Services
{
    public class ExchangeService : RestService
    {
        public ExchangeService(AuthStateProvider authStateProvider, HttpClient httpClient, ISnackbar snackbar)
            : base(authStateProvider, httpClient, snackbar)
        {
        }

        public async Task GetExchangeNamesAsync(
            Func<IEnumerable<string>, Task> onSuccess,
            Func<ApiError, Task>? onError)
        {
            await GetAsync("exchanges/names", onSuccess, onError ?? (x => NotifyError("Get Exchange Names", x)), false);
        }

        public async Task GetFeesAsync(
            Func<ApiExchangeFees, Task> onSuccess,
            Func<ApiError, Task>? onError,
            string exchange,
            string symbolId)
        {
            await GetAsync($"exchanges/{exchange}/fees?symbolId=" + symbolId, onSuccess, onError ?? (x => NotifyError("Get Exchange Fees", x)), true);
        }

        public async Task GetExchangesAsync(
            Func<Page<ApiExchange>, Task> onSuccess,
            Func<ApiError, Task>? onError,
            string? query = null, 
            string? sort = null, 
            string? sortDirection = null,
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
            if (page != null)
                parameters.Add("page", page.ToString()!);
            if (pageSize != null)
                parameters.Add("pageSize", pageSize.ToString()!);

            await GetPageAsync("exchanges", onSuccess, onError ?? (x => NotifyError("Get Exchanges", x)), false, parameters);
        }

        public async Task GetExchangeDetailsAsync(
            Func<ApiExchangeDetails, Task> onSuccess,
            Func<ApiError, Task>? onError,
            string exchange)
        {
            await GetAsync($"exchanges/{exchange}", onSuccess, onError ?? (x => NotifyError("Get Exchange Details", x)), false);
        }

        public async Task GetExchangeEnvironmentsAsync(
            Func<string[], Task> onSuccess,
            Func<ApiError, Task>? onError,
            string exchange)
        {
            await GetAsync($"exchanges/{exchange}/environments", onSuccess, onError ?? (x => NotifyError("Get Exchange Environments", x)), false);
        }
    }
}
