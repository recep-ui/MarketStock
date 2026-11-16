using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Infrastructure.Orders;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Sync;
using PazaryeriHub.Application.Catalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Excel;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Infrastructure.BackgroundJobs;
using PazaryeriHub.Infrastructure.Catalog;
using PazaryeriHub.Infrastructure.Channels;
using PazaryeriHub.Infrastructure.Database;
using PazaryeriHub.Infrastructure.Excel;
using PazaryeriHub.Infrastructure.Stock;
using PazaryeriHub.Infrastructure.Health;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Altyapı servislerini kaydeder. Bağlantı dizesi ve ayarlar kayıt anında değil, servis çözümlenirken okunur.
    /// </summary>
    public static IServiceCollection AddPazaryeriHubInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);
        services.AddOptions<HangfireSettings>().BindConfiguration(HangfireSettings.SectionName);

        services.AddSingleton<SqlConnectionFactory>();
        services.AddSingleton<DatabaseBootstrapper>();
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<IStockLedger, SqlStockLedger>();

        // Genel CRUD: EF Core. Bağlantı dizesi DbContext oluşturulurken (tembel) okunur.
        services.AddDbContext<PazaryeriHubDbContext>((sp, options) =>
            options.UseSqlServer(sp.GetRequiredService<SqlConnectionFactory>().ConnectionString)
                   .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll));
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IChannelService, ChannelService>();
        services.AddScoped<IExcelTransferService, ExcelTransferService>();

        // Siparişler (Dapper). Kanal adaptörleri (IChannelAdapter) bileşim kökünde (Api) kaydedilir.
        services.AddOptions<OrderOptions>().BindConfiguration(OrderOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<OrderService>();
        services.AddScoped<IOrderIngestionService>(sp => sp.GetRequiredService<OrderService>());
        services.AddScoped<IOrderService>(sp => sp.GetRequiredService<OrderService>());

        // Senkron hattı (Outbox → SyncQueue → kanal).
        services.AddOptions<SyncOptions>().BindConfiguration(SyncOptions.SectionName);
        services.AddSingleton<SyncStore>();
        services.AddScoped<ISyncService, SyncService>();

        AddHangfire(services);

        services.AddHealthChecks()
            .AddCheck<SqlServerHealthCheck>("sqlserver", tags: ["ready"], timeout: TimeSpan.FromSeconds(10))
            .AddCheck<HangfireHealthCheck>("hangfire", tags: ["ready"], timeout: TimeSpan.FromSeconds(10));

        return services;
    }

    private static void AddHangfire(IServiceCollection services)
    {
        services.AddHangfire((sp, config) =>
        {
            var connectionFactory = sp.GetRequiredService<SqlConnectionFactory>();
            var settings = sp.GetRequiredService<IOptions<HangfireSettings>>().Value;

            config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                // Microsoft.Data.SqlClient bağlantı fabrikası (System.Data.SqlClient kullanılmaz)
                .UseSqlServerStorage(() => connectionFactory.Create(), new SqlServerStorageOptions
                {
                    SchemaName = settings.SchemaName,
                    PrepareSchemaIfNecessary = true,
                    CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                    QueuePollInterval = TimeSpan.Zero,
                    UseRecommendedIsolationLevel = true,
                    DisableGlobalLocks = true
                });
        });

        services.AddHangfireServer((sp, options) =>
        {
            var settings = sp.GetRequiredService<IOptions<HangfireSettings>>().Value;
            options.ServerName = $"{Environment.MachineName}:{Environment.ProcessId}";
            options.SchedulePollingInterval = TimeSpan.FromSeconds(Math.Max(1, settings.SchedulePollingIntervalSeconds));
            options.ShutdownTimeout = TimeSpan.FromSeconds(10);
            if (settings.WorkerCount > 0)
            {
                options.WorkerCount = settings.WorkerCount;
            }
            if (settings.Queues is { Length: > 0 } queues)
            {
                options.Queues = queues;
            }
        });

        services.AddTransient<OrderFetchJob>();
        services.AddTransient<OutboxProcessorJob>();
        services.AddTransient<StockPushJob>();
        services.AddTransient<BatchCheckJob>();
        services.AddTransient<ReconciliationJob>();
        services.AddHostedService<RecurringJobRegistrar>();
    }
}
