using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Stock;

namespace PazaryeriHub.Api.Errors;

/// <summary>
/// Uygulama istisnalarını sade, Türkçe ProblemDetails yanıtlarına çevirir. Bilinmeyen hatalarda (500) ayrıntı,
/// yığın izi veya SQL metni yanıta YAZILMAZ; yalnızca loglanır.
/// </summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            RequestValidationException v => new ValidationProblemDetails(v.Errors.ToDictionary(e => e.Key, e => e.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Girdi doğrulanamadı."
            },
            NotFoundException nf => Problem(StatusCodes.Status404NotFound, "Kayıt bulunamadı.", nf.Message),
            InvalidOrderTransitionException t => Problem(StatusCodes.Status409Conflict, "Geçersiz durum geçişi.", t.Message),
            ConflictException c => Problem(StatusCodes.Status409Conflict, "İşlem yapılamadı.", c.Message),
            StockOperationException { Code: StockErrorCode.InvalidQuantity } s => WithCode(Problem(StatusCodes.Status400BadRequest, "Geçersiz miktar.", s.Message), s.Code),
            StockOperationException s => WithCode(Problem(StatusCodes.Status409Conflict, "Stok işlemi yapılamadı.", s.Message), s.Code),
            SqlException { Number: 547 } sql when sql.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)
                => Problem(StatusCodes.Status400BadRequest, "Geçersiz başvuru.", "Belirtilen SKU veya depo bulunamadı."),
            BadHttpRequestException b => Problem(b.StatusCode, "İstek okunamadı.", "İstek parametreleri veya gövdesi geçersiz ya da eksik."),
            _ => null
        };

        if (problem is null)
        {
            return false; // varsayılan 500 işleyicisi (ayrıntısız ProblemDetails)
        }

        logger.LogInformation("İstek {Durum} ile reddedildi: {Baslik} {Yol}", problem.Status, problem.Title, httpContext.Request.Path.Value);
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status400BadRequest;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    private static ProblemDetails Problem(int status, string title, string detail) => new() { Status = status, Title = title, Detail = detail };

    private static ProblemDetails WithCode(ProblemDetails problem, StockErrorCode code)
    {
        problem.Extensions["code"] = (int)code;
        return problem;
    }
}
