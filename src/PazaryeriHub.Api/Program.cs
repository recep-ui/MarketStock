using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.Extensions.Options;
using PazaryeriHub.Api.Endpoints;
using PazaryeriHub.Api.Errors;
using PazaryeriHub.Adapters.Mock;
using Microsoft.AspNetCore.DataProtection;
using PazaryeriHub.Api.Auth;
using PazaryeriHub.Api.Channels;
using PazaryeriHub.Api.Dev;
using PazaryeriHub.Api.Health;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Infrastructure.Auth;
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

    // Kanal adaptörleri: "Kanallar:{KOD}:Mod" = Mock (varsayılan) | Gercek. Şimdilik yalnızca Trendyol'un gerçek adaptörü var.
    builder.Services.AddChannelAdapters(builder.Configuration);

    // Kanal API kimlik bilgileri IDataProtector ile şifrelenir (ADR-016). Anahtar halkası varsayılan olarak kullanıcı profilinde;
    // sunucu/konteyner için "DataProtection:KeysPath" ile kalıcı bir klasör verilmelidir.
    var dataProtection = builder.Services.AddDataProtection().SetApplicationName("PazaryeriHub");
    if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    {
        dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
    }

    // Panel kimlik doğrulama ve rol tabanlı yetki (ADR-014)
    builder.Services.AddPanelAuth();
    builder.Services.AddScoped<DemoDataSeeder>();

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

    // Tohum kullanıcılar (Kimlik:SeedUsers; parolalar user-secrets/ortam değişkeninden). Veritabanı yoksa uygulamayı düşürmez.
    try
    {
        await app.Services.GetRequiredService<UserService>().SeedAsync(app.Environment, app.Lifetime.ApplicationStopping);
    }
    catch (Microsoft.Data.SqlClient.SqlException ex)
    {
        app.Logger.LogError("Tohum kullanıcılar uygulanamadı (SQL hata {HataNo}).", ex.Number);
    }

    var panelServed = app.UsePanelStaticFiles();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapPazaryeriHubHealthChecks();
    app.MapAuthEndpoints();
    app.MapPanelEndpoints();
    app.MapCatalogEndpoints();
    app.MapChannelEndpoints();
    app.MapOrderEndpoints(app.Environment);
    app.MapSyncEndpoints();

    if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
    {
        app.MapPost("/api/dev/demo-data", (DemoDataSeeder seeder, CancellationToken ct) => seeder.SeedAsync(ct))
            .RequireAuthorization(Policies.Admin).WithTags("Geliştirme");
    }

    if (app.Environment.IsDevelopment())
    {
        // Hangfire paneli yalnızca geliştirme ortamında ve yalnızca yerel isteklere açık (Hangfire varsayılanı).
        app.UseHangfireDashboard("/hangfire");
    }

    if (!panelServed)
    {
        app.MapGet("/", () => Results.Ok(new { uygulama = "PazaryeriHub.Api", saglik = "/health", panel = "derlenmemiş (panel/ klasöründe npm run build)" }));
    }

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
