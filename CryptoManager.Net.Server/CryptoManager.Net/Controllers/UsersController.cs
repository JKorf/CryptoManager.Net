using CryptoExchange.Net.Objects.Errors;
using CryptoManager.Net.ApiModels.Requests;
using CryptoManager.Net.Auth;
using CryptoManager.Net.Database;
using CryptoManager.Net.Database.Models;
using CryptoManager.Net.Models.Response;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoManager.Net.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class UsersController : ApiController
    {
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;
        private readonly JwtService _jwtService;
        private readonly AuthService _authService;

        public UsersController(ILogger<UsersController> logger, IConfiguration configuration, JwtService jwtService, AuthService authService, TrackerContext dbContext) : base(dbContext)
        {
            _logger = logger;
            _authService = authService;
            _jwtService = jwtService;
            _configuration = configuration;
        }

        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<ApiResult<ApiUser>> LoginAsync(LoginRequest request)
        {
            var user = await _dbContext.Users.Include(x => x.ApiKeys).SingleOrDefaultAsync(x => x.Email == request.Email);
            if (user == null)
            {
                _logger.LogDebug("Login failed for {Email}, no user found", request.Email);
                return ApiResult<ApiUser>.Error(ApiErrors.LoginFailed);
            }

            var hasher = new PasswordHasher<User>();
            var result = hasher.VerifyHashedPassword(user, user.Password, request.Password);
            if (result == PasswordVerificationResult.Failed)
            {
                _logger.LogDebug("Login failed for {Email}, password doesn't match", request.Email);
                return ApiResult<ApiUser>.Error(ApiErrors.LoginFailed);
            }

            _logger.LogDebug("Successful login for {Email}", request.Email);
            var jwt = _jwtService.GenerateJwtToken(user);
            var refreshToken = await _authService.CreateRefreshTokenAsync(user.Id);

            Response.Cookies.Append("refreshToken", refreshToken.Token, new CookieOptions
            {
                Expires = refreshToken.ExpireTime,
                HttpOnly = true,
                SameSite = SameSiteMode.None,
                Secure = true
            });

            return ApiResult<ApiUser>.Ok(new ApiUser
            {
                Id = user.Id,
                Email = request.Email,
                AuthenticatedExchanges = user.ApiKeys.Select(x => x.Exchange).ToArray(),
                Jwt = jwt
            });
        }

        [HttpPost("refresh")]
        [AllowAnonymous]
        public async Task<ApiResult<ApiUser>> RefreshAsync()
        {
            var refreshToken = Request.Cookies["refreshToken"];
            if (string.IsNullOrEmpty(refreshToken))
            {
                _logger.LogDebug("Refresh token not in request");
                return ApiResult<ApiUser>.Error(ApiErrors.RefreshFailed);
            }

            var dbToken = await _dbContext.RefreshTokens.SingleOrDefaultAsync(x => x.Token == refreshToken);
            if (dbToken == null || dbToken.ExpireTime < DateTime.UtcNow)
            {
                _logger.LogDebug("Refresh token not found in database");
                return ApiResult<ApiUser>.Error(ApiErrors.RefreshFailed);
            }

            var user = await _dbContext.Users.Include(x => x.ApiKeys).SingleAsync(x => x.Id == dbToken.UserId);
            var jwt = _jwtService.GenerateJwtToken(user);
            var newRefreshToken = await _authService.RefreshRefreshTokenAsync(dbToken.Id);

            Response.Cookies.Append("refreshToken", newRefreshToken.Token, new CookieOptions
            {
                Expires = newRefreshToken.ExpireTime,
                HttpOnly = true,
                SameSite = SameSiteMode.None,
                Secure = true
            });

            _logger.LogDebug("Successful refreshed token for {Email}", user.Email);
            return ApiResult<ApiUser>.Ok(new ApiUser
            {
                Id = user.Id,
                Email = user.Email,
                AuthenticatedExchanges = user.ApiKeys.Select(x => x.Exchange).ToArray(),
                Jwt = jwt
            });
        }

        [HttpPost("logout")]
        public async Task<ApiResult> LogoutAsync()
        {
            var refreshToken = Request.Cookies["refreshToken"];
            if (string.IsNullOrEmpty(refreshToken))
            {
                _logger.LogDebug("Refresh token not in request");
                return ApiResult.Ok();
            }

            var dbToken = await _dbContext.RefreshTokens.SingleOrDefaultAsync(x => x.Token == refreshToken);
            if (dbToken == null || dbToken.ExpireTime < DateTime.UtcNow)
            {
                _logger.LogDebug("Refresh token not found in database");
                return ApiResult<ApiUser>.Error(ApiErrors.RefreshFailed);
            }

            _dbContext.RefreshTokens.Remove(dbToken);
            await _dbContext.SaveChangesAsync();

            _logger.LogDebug("Successful logout for {Id}", dbToken.UserId);
            return ApiResult.Ok();
        }

        [HttpGet]
        [ResponseCache(NoStore = true)]
        public async Task<ApiResult<ApiUser>> GetUserAsync()
        {
            var user = await _dbContext.Users.Include(x => x.ApiKeys).SingleAsync(x => x.Id == UserId);

            return ApiResult<ApiUser>.Ok(new ApiUser
            {
                Id = user.Id,
                Email = user.Email,
                AuthenticatedExchanges = user.ApiKeys.Select(x => x.Exchange).ToArray()
            });
        }

        [HttpPost("register")]
        [AllowAnonymous]
        public async Task<ApiResult<ApiUser>> RegisterAsync(RegisterRequest request)
        {
            if (!_configuration.GetValue<bool>("RegistrationEnabled"))
                return ApiResult<ApiUser>.Error(ApiErrors.RegistrationDisabled);

            if (request.Email.Length < 6)
                return ApiResult<ApiUser>.Error(ErrorType.Unknown, null, "Email too short");

            if (request.Password.Length < 8)
                return ApiResult<ApiUser>.Error(ErrorType.Unknown, null, "Password too short");

            var user = await _dbContext.Users.SingleOrDefaultAsync(x => x.Email == request.Email);
            if (user != null)
            {
                _logger.LogDebug("Register failed for {Email}, user already exists", request.Email);
                return ApiResult<ApiUser>.Error(ErrorType.Unknown, null, "User with email already exists");
            }

#warning there should probably be an email verification

            user = new User
            {
                Email = request.Email,
                CreateTime = DateTime.UtcNow,
                UpdateTime = DateTime.UtcNow
            };
            var hasher = new PasswordHasher<User>();
            user.Password = hasher.HashPassword(user, request.Password);
            _dbContext.Users.Add(user);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Successful user registration for {Email}", request.Email);
            var jwt = _jwtService.GenerateJwtToken(user);
            var refreshToken = await _authService.CreateRefreshTokenAsync(user.Id);

            Response.Cookies.Append("refreshToken", refreshToken.Token, new CookieOptions
            {
                Expires = refreshToken.ExpireTime,
                HttpOnly = true,
                SameSite = SameSiteMode.None,
                Secure = true
            });

            return ApiResult<ApiUser>.Ok(new ApiUser
            {
                Id = user.Id,
                Email = user.Email,
                Jwt = jwt
            });
        }
    }
}
