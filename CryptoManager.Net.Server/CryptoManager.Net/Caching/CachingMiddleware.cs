using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;

namespace CryptoManager.Net.Caching
{
    public class CachingMiddleware : IMiddleware
    {
        private readonly ILogger _logger;
        private readonly ITrackerCache _cache;

        public CachingMiddleware(ILogger<CachingMiddleware> logger, ITrackerCache cache)
        {
            _logger = logger;
            _cache = cache;
        }

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (context.Request.Method != "GET")
            {
                await next(context);
                return;
            }

            var cacheAttr = (ServerCacheAttribute?)context.Features.Get<IEndpointFeature>()?.Endpoint?.Metadata.SingleOrDefault(x => x is ServerCacheAttribute);
            if (cacheAttr == null)
            {
                await next(context);
                return;
            }

            var allowAnon = context.Features.Get<IEndpointFeature>()?.Endpoint?.Metadata.Any(x => x is AllowAnonymousAttribute) == true;
            var key = context.Request.Path + context.Request.QueryString.ToString();
            if (!allowAnon)
            {
                context.Items.TryGetValue("userId", out var userId);
                key = userId + key;
            }

            var cached = _cache.TryGet<string>(key);
            if (cached != null)
            {
                context.Response.ContentType = "application/json;";
                await context.Response.WriteAsync(cached);
                return;
            }

            var originalResponseStream = context.Response.Body;
            var tempResponseStream = new MemoryStream();
            context.Response.Body = tempResponseStream;
            await next(context);
            tempResponseStream.Position = 0;
            using var reader = new StreamReader(tempResponseStream);
            var responseJson = await reader.ReadToEndAsync();
            _cache.Set(key, responseJson, TimeSpan.FromSeconds(cacheAttr.Duration));
            tempResponseStream.Position = 0;
            await tempResponseStream.CopyToAsync(originalResponseStream);

        }
    }
}
