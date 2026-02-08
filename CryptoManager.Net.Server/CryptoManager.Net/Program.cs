using Azure.Monitor.OpenTelemetry.AspNetCore;
using CryptoManager.Net;
using CryptoManager.Net.Analyzers;
using CryptoManager.Net.Auth;
using CryptoManager.Net.Caching;
using CryptoManager.Net.Database;
using CryptoManager.Net.Models;
using CryptoManager.Net.Processor.FiatPrices;
using CryptoManager.Net.Processor.Symbols;
using CryptoManager.Net.Processor.Tickers;
using CryptoManager.Net.Publish;
using CryptoManager.Net.Publisher.FiatPrices;
using CryptoManager.Net.Publisher.Symbols;
using CryptoManager.Net.Publisher.Tickers;
using CryptoManager.Net.Services;
using CryptoManager.Net.Subscriptions.OrderBook;
using CryptoManager.Net.Subscriptions.Tickers;
using CryptoManager.Net.Subscriptions.Trades;
using CryptoManager.Net.Subscriptions.User;
using CryptoManager.Net.Websockets;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers().AddJsonOptions(x =>
{
    x.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddOpenApi();
builder.Services.AddResponseCaching();

builder.Services.AddCryptoClients(options =>
{
    options.CachingEnabled = false;
});

builder.Services.AddSingleton<IPublishOutput<Symbol>, ChannelSymbolPublishOutput>(x => new ChannelSymbolPublishOutput(ChannelRegistrations.SymbolChannel.Writer));
builder.Services.AddSingleton<IPublishOutput<Ticker>, ChannelTickerPublishOutput>(x => new ChannelTickerPublishOutput(ChannelRegistrations.TickerChannel.Writer));
builder.Services.AddSingleton<IPublishOutput<FiatPrice>, ChannelFiatPricePublishOutput>(x => new ChannelFiatPricePublishOutput(ChannelRegistrations.FiatPriceChannel.Writer));
builder.Services.AddSingleton<IPublishOutput<PendingAssetCalculation>, ChannelAssetCalculationPublishOutput>(x => new ChannelAssetCalculationPublishOutput(ChannelRegistrations.AssetCalculationChannel.Writer));
builder.Services.AddSingleton<IProcessInput<Symbol>, ChannelSymbolProcessInput>(x => new ChannelSymbolProcessInput(ChannelRegistrations.SymbolChannel.Reader));
builder.Services.AddSingleton<IProcessInput<Ticker>, ChannelTickerProcessInput>(x => new ChannelTickerProcessInput(ChannelRegistrations.TickerChannel.Reader));
builder.Services.AddSingleton<IProcessInput<FiatPrice>, ChannelFiatPriceProcessInput>(x => new ChannelFiatPriceProcessInput(ChannelRegistrations.FiatPriceChannel.Reader));
builder.Services.AddSingleton<IProcessInput<PendingAssetCalculation>, ChannelAssetCalculationProcessInput>(x => new ChannelAssetCalculationProcessInput(ChannelRegistrations.AssetCalculationChannel.Reader));
builder.Services.AddAsSingletonAndBackgroundService<SymbolsProcessService>();
builder.Services.AddAsSingletonAndBackgroundService<SymbolPublishService>();
builder.Services.AddAsSingletonAndBackgroundService<TickerProcessService>();
builder.Services.AddAsSingletonAndBackgroundService<TickerPublishService>();
builder.Services.AddAsSingletonAndBackgroundService<FiatPricesProcessService>();
builder.Services.AddAsSingletonAndBackgroundService<FiatPricePublishService>();
builder.Services.AddAsSingletonAndBackgroundService<AssetCalculationProcessService>();
builder.Services.AddAsSingletonAndBackgroundService<SymbolUsdVolumeService>();
builder.Services.AddAsSingletonAndBackgroundService<UserPortfolioSnapshotService>();
builder.Services.AddAsSingletonAndBackgroundService<AssetStatsAnalyzerService>();
builder.Services.AddSingleton<TickerSubscriptionService>();
builder.Services.AddSingleton<TradeSubscriptionService>();
builder.Services.AddSingleton<OrderBookSubscriptionService>();
builder.Services.AddSingleton<UserSubscriptionService>();

builder.Services.AddTransient<JwtService>();
builder.Services.AddTransient<AuthService>();
builder.Services.AddTransient<AuthMiddleware>();

builder.Services.AddTransient<CachingMiddleware>();
builder.Services.AddSingleton<ITrackerCache, TrackerCache>();

builder.Services.AddHostedService<MigrationManager>();
builder.Services.AddHostedService<BackgroundServiceManager>();
builder.Services.AddAsSingletonAndHostedService<WebsocketManager>();

builder.Services.AddDbContextFactory<TrackerContext>(efOptions =>
{
    efOptions.UseSqlServer(builder.Configuration.GetConnectionString("Tracker"), sqlServerOptions =>
    {
        sqlServerOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery);
        sqlServerOptions.EnableRetryOnFailure();
    });
});

builder.Services.Configure<HostOptions>(options =>
{
    options.ServicesStartConcurrently = false;
});

builder.Services.AddCors(x =>
{
    x.AddPolicy("Default", x =>
    {
        x.WithOrigins(builder.Configuration.GetValue<string>("CorsOrigins")!.Split(';', StringSplitOptions.RemoveEmptyEntries));
        x.AllowAnyMethod();
        x.AllowAnyHeader();
        x.AllowCredentials();
    });
});

#if !DEBUG
builder.Services.AddOpenTelemetry().UseAzureMonitor(x =>
{
    x.SamplingRatio = 0.02f;
});
#endif
var app = builder.Build();

app.UseCors("Default");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseMiddleware<AuthMiddleware>();
app.UseMiddleware<CachingMiddleware>();

app.UseResponseCaching();
app.UseAuthentication();

app.UseHttpsRedirection();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    KeepAliveTimeout = TimeSpan.FromSeconds(10)
});

app.UseAuthorization();

app.MapControllers();

app.Run();