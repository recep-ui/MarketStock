using Hangfire;
using PazaryeriHub.Api.Health;
using PazaryeriHub.Infrastructure;
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

    var app = builder.Build();

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

    app.MapPazaryeriHubHealthChecks();

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
