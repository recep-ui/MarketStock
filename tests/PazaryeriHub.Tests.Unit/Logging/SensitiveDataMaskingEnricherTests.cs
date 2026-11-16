using PazaryeriHub.Infrastructure.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PazaryeriHub.Tests.Unit.Logging;

public sealed class SensitiveDataMaskingEnricherTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (Logger Logger, CollectingSink Sink) CreateLogger()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .Enrich.With(new SensitiveDataMaskingEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("ApiKey")]
    [InlineData("api-key")]
    [InlineData("Authorization")]
    [InlineData("KimlikSifreli")]
    [InlineData("MusteriTelefon")]
    [InlineData("Email")]
    public void Hassas_adli_ozellik_maskelenir(string propertyName)
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Değer {" + propertyName.Replace("-", "_") + "}", "cok-gizli-deger");

        var evt = sink.Events.Should().ContainSingle().Subject;
        evt.RenderMessage().Should().NotContain("cok-gizli-deger");
        evt.Properties.Values.Should().ContainSingle()
            .Which.Should().BeOfType<ScalarValue>()
            .Which.Value.Should().Be(SensitiveDataMaskingEnricher.Mask);
    }

    [Fact]
    public void Hassas_olmayan_ozellik_degismez()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Kanal {Kanal} için {Adet} sipariş", "TRENDYOL", 5);

        sink.Events.Single().RenderMessage().Should().Be("Kanal \"TRENDYOL\" için 5 sipariş");
    }

    [Fact]
    public void Ic_ice_nesnedeki_hassas_alan_maskelenir_digerleri_korunur()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("İstek {@Istek}", new { Kanal = "HEPSIBURADA", ApiSecret = "s3cr3t", Musteri = new { Ad = "Ali", Telefon = "5551112233" } });

        var rendered = sink.Events.Single().RenderMessage();
        rendered.Should().NotContain("s3cr3t").And.NotContain("5551112233");
        rendered.Should().Contain("HEPSIBURADA").And.Contain("Ali");
    }

    [Fact]
    public void Sozlukteki_hassas_anahtar_maskelenir()
    {
        var (logger, sink) = CreateLogger();
        var headers = new Dictionary<string, string> { ["Authorization"] = "Basic dXNlcjpwYXNz", ["User-Agent"] = "PazaryeriHub" };

        logger.Information("Başlıklar {@Basliklar}", headers);

        var rendered = sink.Events.Single().RenderMessage();
        rendered.Should().NotContain("dXNlcjpwYXNz").And.Contain("PazaryeriHub");
    }

    [Fact]
    public void Metin_icindeki_baglanti_dizesi_parolasi_maskelenir()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Bağlanılıyor: {Hedef}", "Data Source=10.0.0.1,1434;User ID=test_user;Password=Sahte_Parola_42;Encrypt=True");

        var rendered = sink.Events.Single().RenderMessage();
        rendered.Should().NotContain("Sahte_Parola_42");
        rendered.Should().Contain("Password=***").And.Contain("Encrypt=True");
    }

    [Fact]
    public void Metin_icindeki_bearer_token_maskelenir()
    {
        SensitiveDataMaskingEnricher.MaskText("hata: Bearer eyJhbGciOiJIUzI1NiJ9.abc.def geçersiz")
            .Should().Be("hata: Bearer *** geçersiz");
    }

    [Fact]
    public void Dizi_icindeki_nesnelerde_maskeleme_yapilir()
    {
        var (logger, sink) = CreateLogger();

        logger.Information("Liste {@Liste}", new[] { new { Sku = "A1", Token = "t-1" }, new { Sku = "B2", Token = "t-2" } });

        var rendered = sink.Events.Single().RenderMessage();
        rendered.Should().NotContain("t-1").And.NotContain("t-2").And.Contain("A1").And.Contain("B2");
    }
}
