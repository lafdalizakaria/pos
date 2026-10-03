using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Domain.Common;

namespace Newrest.Pos.Api;

/// <summary>Maps business errors to RFC 9457 problem details with a stable <c>code</c> extension.</summary>
public sealed partial class DomainExceptionHandler(IProblemDetailsService problemDetails, ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, detail) = exception switch
        {
            DomainException d => (StatusFor(d.Code), d.Code, d.Message),
            DbUpdateException => (StatusCodes.Status409Conflict, "conflict", "The change conflicts with existing data (duplicate key or concurrent update)."),
            BadHttpRequestException b => (b.StatusCode, "bad_request", b.Message),
            _ => (0, string.Empty, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        if (exception is DbUpdateException)
        {
            LogConflict(logger, exception);
        }

        if (httpContext.Request.Path.StartsWithSegments("/api/v1/register"))
        {
            Application.Operations.PosMetrics.SyncRejections.Add(1, new KeyValuePair<string, object?>("code", code));
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = code,
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        });
    }

    public static int StatusFor(string code) => code switch
    {
        "not_found" => StatusCodes.Status404NotFound,
        "forbidden" => StatusCodes.Status403Forbidden,
        "conflict" or "idempotency_conflict" or "already_reversed" or "immutable_record" or "duplicate_menu_item"
            or "badge_already_active" or "same_badge_number" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database conflict")]
    private static partial void LogConflict(ILogger logger, Exception exception);
}
