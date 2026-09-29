using Gecko.Data;
using Gecko.SharedKernel;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Gecko.Api;

/// <summary>
/// Maps the failures that are the CALLER's fault to 4xx problem details, so they
/// never surface as a 500 with a stack trace. Anything unmapped stays a 500.
/// </summary>
internal sealed class GeckoExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        (int status, string title)? mapped = exception switch
        {
            // Binding failures (missing ?branchId=, malformed Guid). Development throws
            // them (RouteHandlerOptions.ThrowOnBadRequest); Production answers 400 itself.
            BadHttpRequestException e => (e.StatusCode, e.Message),
            MissingTenantContextException => (StatusCodes.Status401Unauthorized, "No tenant in the access token."),
            InvalidStateTransitionException e => (StatusCodes.Status409Conflict, e.Message),
            DomainException e => (StatusCodes.Status422UnprocessableEntity, e.Message),
            DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } => (StatusCodes.Status409Conflict, "A record with the same unique value already exists."),
            DbUpdateException { InnerException: SqlException { Number: 547 } } => (StatusCodes.Status422UnprocessableEntity, "The value violates a database rule (CHECK constraint)."),
            DbUpdateException { InnerException: SqlException { Number: 33504 } } => (StatusCodes.Status403Forbidden, "The row does not belong to your tenant."),
            _ => null,
        };

        if (mapped is null) return false;

        http.Response.StatusCode = mapped.Value.status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails = { Status = mapped.Value.status, Title = mapped.Value.title },
        });
    }
}
