using CryptoClients.Net;
using CryptoClients.Net.Interfaces;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using CryptoExchange.Net.SharedApis;
using CryptoManager.Net.ApiModels.Requests;
using CryptoManager.Net.Database;
using CryptoManager.Net.Database.Models;
using CryptoManager.Net.Models.Response;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace CryptoManager.Net.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ApiKeysController : ApiController
    {
        private readonly ILogger _logger;
        private readonly IExchangeUserClientProvider _clientProvider;

        public ApiKeysController(ILogger<ApiKeysController> logger, TrackerContext dbContext, IExchangeUserClientProvider clientProvider) : base(dbContext)
        {
            _logger = logger;
            _clientProvider = clientProvider;
        }

        [HttpGet]
        public async Task<ApiResultPaged<IEnumerable<ApiApiKey>>> ListAsync(int page = 1, int pageSize = 10)
        {
            var query = _dbContext.UserApiKeys.Where(x => x.UserId == UserId).OrderBy(x => x.Exchange);

            var total = await query.CountAsync();
            var keys = await query.Skip((page - 1) * pageSize).Take(pageSize).AsNoTracking().ToListAsync();
            return ApiResultPaged<IEnumerable<ApiApiKey>>.Ok(page, pageSize, total, keys.Select(x => new ApiApiKey
            {
                Id = x.Id,
                Exchange = x.Exchange,
                Environment = x.Environment!,
                Key = x.Key,
                Invalid = x.Invalid
            }));
        }

        [HttpGet("configured")]
        public async Task<ApiResult<string[]>> GetApiKeysConfiguredAsync()
        {
            var query = await _dbContext.UserApiKeys.Where(x => x.UserId == UserId).OrderBy(x => x.Exchange).ToListAsync();

            return ApiResult<string[]>.Ok(query.Select(x => x.Exchange).ToArray());
        }

        [HttpPost]
        public async Task<ApiResult<int>> AddAsync(AddApiKeyRequest request)
        {
            var existingKey = await _dbContext.UserApiKeys.Where(x => x.UserId == UserId && x.Exchange == request.Exchange).AsNoTracking().SingleOrDefaultAsync();
            if (existingKey != null)
            {
                _logger.LogDebug("Key already exists for user {User} on exchange {Exchange}", UserId, request.Exchange);
                return ApiResult<int>.Error(ErrorType.InvalidOperation, null, $"Key already exists for for exchange {request.Exchange}");
            }

            var secret = WebUtility.UrlDecode(request.Secret)
                .Replace(' ', '+') // + char gets decoded to space
                .Replace("\\n", "\n");
            var newKey = new UserApiKey
            {
                UserId = UserId,
                Exchange = request.Exchange,
                Environment = request.Environment ?? TradeEnvironmentNames.Live,
                Key = request.Key,
                Secret = secret,
                Pass = request.Pass
            };
            _dbContext.UserApiKeys.Add(newKey);
            await _dbContext.SaveChangesAsync();

            _clientProvider.ClearUserClients(UserId.ToString(), request.Exchange);
            return ApiResult<int>.Ok(newKey.Id);
        }

        [HttpPost("validate/{id}")]
        public async Task<ApiResult<bool>> ValidateAsync(int id)
        {
            var existingKey = await _dbContext.UserApiKeys.Where(x => x.UserId == UserId && x.Id == id).SingleOrDefaultAsync();
            if (existingKey == null)
            {
                _logger.LogDebug("Key {id} not found for user {User}", id, UserId);
                return ApiResult<bool>.Error(ErrorType.Unknown, "404", $"Key not found");
            }

            if (existingKey.Invalid)
            {
                _logger.LogDebug("Key {id} for user {User} already marked as invalid", id, UserId);
                return ApiResult<bool>.Error(new ApiError(ErrorType.Unauthorized, null, "Invalid credentials"));
            }

            var environments = new Dictionary<string, string?> { { existingKey.Exchange, existingKey.Environment } };
            var client = new ExchangeRestClient(options => options.ApiEnvironments = environments);
            client.SetApiCredentials(existingKey.Exchange, existingKey.Key, existingKey.Secret, existingKey.Pass);

            var balanceClient = client.GetBalancesClient(TradingMode.Spot, existingKey.Exchange);
            if (balanceClient == null)
                return ApiResult<bool>.Error(new ApiError(ErrorType.InvalidOperation, null, "Balance client not available"));

            var result = await balanceClient.GetBalancesAsync(new GetBalancesRequest());
            if (!result.Success)
            {
                existingKey.Invalid = true;
                await _dbContext.SaveChangesAsync();
                return ApiResult<bool>.Error(new ApiError(ErrorType.Unauthorized, null, "Invalid credentials"));
            }

            return ApiResult<bool>.Ok(result.Success);
        }

        [HttpPost("validate")]
        public async Task<ApiResult<bool>> ValidateAsync(string exchange, string environment, string apiKey, string apiSecret, string? apiPass = null)
        {
            apiSecret = WebUtility.UrlDecode(apiSecret)
                .Replace(' ', '+') // + char gets decoded to space
                .Replace("\\n", "\n");
            var client = new ExchangeRestClient(options => options.ApiEnvironments = new Dictionary<string, string?> { { exchange, environment } });
            try
            {
                client.SetApiCredentials(exchange, apiKey, apiSecret, apiPass);
            }
            catch (ArgumentException aex)
            {
                if (aex.ParamName == "apiPass")
                    return ApiResult<bool>.Error(ApiErrors.PassphraseRequired);

                return ApiResult<bool>.Error(new ApiError(ErrorType.Unauthorized, null, "Invalid credentials"));
            }
            catch (Exception)
            {
                return ApiResult<bool>.Error(new ApiError(ErrorType.Unauthorized, null, "Invalid credentials"));
            }

            var balanceClient = client.GetBalancesClient(TradingMode.Spot, exchange);
            if (balanceClient == null)
                return ApiResult<bool>.Error(new ApiError(ErrorType.InvalidOperation, null, "Balance client not available"));

            var result = await balanceClient.GetBalancesAsync(new GetBalancesRequest());
            if (!result.Success)
                return ApiResult<bool>.Error(new ApiError(ErrorType.Unauthorized, null, "Invalid credentials"));

            return ApiResult<bool>.Ok(result.Success);
        }

        [HttpDelete("{id}")]
        public async Task<ApiResult> DeleteAsync(int id)
        {
            var existingKey = await _dbContext.UserApiKeys.Where(x => x.UserId == UserId && x.Id == id).SingleOrDefaultAsync();
            if (existingKey == null)
                return ApiResult.Ok();

            _dbContext.UserApiKeys.Remove(existingKey);
            await _dbContext.SaveChangesAsync();

            return ApiResult.Ok();
        }
    }
}
