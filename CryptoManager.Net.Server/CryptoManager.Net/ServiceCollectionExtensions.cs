using CryptoManager.Net.Publish;

namespace CryptoManager.Net
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddAsSingletonAndBackgroundService<T>(this IServiceCollection services) where T : class, IBackgroundService
        {
            services.AddSingleton<T>();
            services.AddSingleton<IBackgroundService>(x => x.GetRequiredService<T>());
            return services;
        }

        public static IServiceCollection AddAsSingletonAndHostedService<T>(this IServiceCollection services) where T : class, IHostedService
        {
            services.AddSingleton<T>();
            services.AddHostedService(x => x.GetRequiredService<T>());
            return services;
        }
    }
}
