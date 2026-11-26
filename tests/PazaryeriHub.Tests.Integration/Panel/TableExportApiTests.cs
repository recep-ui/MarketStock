using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Panel;

/// <summary>
/// ADR-060: genel Excel dışa aktarma ucu. Oturum gerekir; tüm roller kullanabilir (veri, rolün liste uçlarından zaten aldığı veridir);
/// geçersiz istek 400 döner; dosya adı güvenli hale getirilir.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TableExportApiTests(TestDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string fileName = "stok-listesi") => new
    {
        fileName,
        sheetName = "Stok Listesi",
        columns = new object[] { new { header = "SKU" }, new { header = "Satılabilir", kind = "number" } },
        rows = new object[] { new object?[] { "A-1", 5 }, new object?[] { "B-2", null } }
    };

    [Fact]
    public async Task Oturumsuz_istek_401()
    {
        db.SkipIfUnavailable();
        using var anonymous = db.ApiHost.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/export/xlsx", Body(), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Warehouse)]
    [InlineData(Roles.Accounting)]
    [InlineData(Roles.Support)]
    public async Task Her_rol_gonderdigi_satirlari_xlsx_olarak_alir(string role)
    {
        db.SkipIfUnavailable();
        using var client = await db.ApiHost.CreateClientAsAsync(role);

        var response = await client.PostAsJsonAsync("/api/export/xlsx", Body("../stok listesi"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var fileName = response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName!;
        fileName.Should().StartWith("stok-listesi-").And.EndWith(".xlsx");

        using var wb = new XLWorkbook(await response.Content.ReadAsStreamAsync(Ct));
        var sheet = wb.Worksheet(1);
        sheet.Name.Should().Be("Stok Listesi");
        sheet.Cell(1, 2).GetString().Should().Be("Satılabilir");
        sheet.Cell(2, 1).GetString().Should().Be("A-1");
        sheet.Cell(2, 2).GetDouble().Should().Be(5);
        sheet.Cell(3, 2).IsEmpty().Should().BeTrue();
    }

    [Fact]
    public async Task Gecersiz_istek_400_ve_sade_mesaj()
    {
        db.SkipIfUnavailable();
        var response = await db.Api.PostAsJsonAsync("/api/export/xlsx", new { columns = Array.Empty<object>(), rows = Array.Empty<object>() }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("sütun");
    }
}
