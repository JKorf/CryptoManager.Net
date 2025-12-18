using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using System.IdentityModel.Tokens.Jwt;

namespace CryptoManager.Net.Auth
{
    public class AuthMiddleware : IMiddleware
    {
        private readonly ILogger _logger;
        private readonly JwtService _jwtService;

        public AuthMiddleware(ILogger<AuthMiddleware> logger, JwtService jwtService)
        {
            _logger = logger;
            _jwtService = jwtService;
        }

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (context.Request.Method == HttpMethod.Options.Method
                || context.Request.Method == HttpMethod.Connect.Method)
            {
                await next(context);
                return;
            }

            var allowAnon = context.Features.Get<IEndpointFeature>()?.Endpoint?.Metadata.Any(x => x is AllowAnonymousAttribute);
            if (allowAnon == true)
            {
                await next(context);
                return;
            }

            // Get JWT from user and get user id
            var header = context.Request.Headers.Authorization.SingleOrDefault();
            if (header == null)
            {
                _logger.LogDebug("Request denied, no JWT found");
                context.Response.StatusCode = 401;
                return;
            }

            var headerParts = header.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (headerParts.Length != 2)
            {
                _logger.LogDebug("Request denied, JWT format invalid");
                context.Response.StatusCode = 401;
                return;
            }

            var jwt = headerParts[1];
            JwtSecurityToken token;
            try
            {
                token = _jwtService.ValidateToken(jwt);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Request denied, JWT invalid");
                context.Response.StatusCode = 401;
                return;
            }

            var userId = int.Parse(token.Claims.First(x => x.Type == "id").Value);
            context.Items["userId"] = userId;
            await next(context);
        }
    }
}
