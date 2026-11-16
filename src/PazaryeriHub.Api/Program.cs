using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.Extensions.Options;
using PazaryeriHub.Api.Endpoints;
using PazaryeriHub.Api.Errors;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Api.Health;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Infrastructure;
using PazaryeriHub.Infrastructure.Database;
using PazaryeriHub.Infrastructure.Logging;
using PazaryeriHub.Infrastructure.Persistence;
using Serilog;
using Serilog.Events;

// Başlangıç hataları için geçici konsol logger'ı. Asıl logger aşağıda yapılandırmadan kurulur.
Log.Logger = new LoggerConfiguration()
    .Enrich.With(new SensitiveDataMaskingEnricher())
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog(
        (services, lc) => lc
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.With(new SensitiveDataMaskingEnricher()),
        preserveStaticLogger: true);

    builder.Services.AddPazaryeriHubInfrastructure();

    // Kanal adaptörleri. Gerçek adaptörler (Aşama 6) doğrulanana kadar tüm kanallar bellek içi MOCK ile çalışır.
    // TODO: doğrulanmalı — Trendyol/Hepsiburada/Beymen/softcotton gerçek entegrasyonları.
    builder.Services.AddSingleton<MockChannelStore>();
    foreach (var code in ChannelCodes.All)
    {
        builder.Services.AddSingleton<IChannelAdapter>(sp => new MockChannelAdapter(code, sp.GetRequiredService<MockChannelStore>()));
    }

    // Hatalar Türkçe ProblemDetails olarak döner; 500'lerde ayrıntı sızdırılmaz.
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<ApiExceptionHandler>();
    builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    var app = builder.Build();

    app.UseExceptionHandler();
    app.UseStatusCodePages();

    app.UseSerilogRequestLogging(options =>
    {
        // Sağlık kontrolü istekleri logu kirletmesin.
        options.GetLevel = (ctx, _, ex) =>
            ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
            : ctx.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
            : LogEventLevel.Information;
    });

    // Geliştirme ortamında veritabanı yoksa oluşturulur (Database:AutoCreate). Hata uygulamayı düşürmez.
    await app.Services.GetRequiredService<DatabaseBootstrapper>().TryEnsureDatabaseAsync(app.Lifetime.ApplicationStopping);

    // Şema migration'ları (Database:MigrateOnStartup). Başarısızsa uygulama başlamaz: şemasız çalışmak anlamsız.
    if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
    {
        app.Services.GetRequiredService<DatabaseMigrator>()
            .Migrate(app.Services.GetRequiredService<SqlConnectionFactory>().ConnectionString);
    }

    app.MapPazaryeriHubHealthChecks();
    app.MapCatalogEndpoints();
    app.MapChannelEndpoints();
    app.MapOrderEndpoints(app.Environment);

    if (app.Environment.IsDevelopment())
    {
        // Yetkilendirme Aşama 5'te eklenecek; o zamana kadar yalnızca geliştirme ortamında ve yerelden erişilir.
        app.UseHangfireDashboard("/hangfire");
    }

    app.MapGet("/", () => Results.Ok(new { uygulama = "PazaryeriHub.Api", saglik = "/health" }));

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Uygulama beklenmedik şekilde sonlandı.");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entegrasyon testlerinde WebApplicationFactory için erişilebilir giriş noktası.</summary>
public partial class Program;
