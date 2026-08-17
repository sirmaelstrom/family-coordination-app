using Microsoft.AspNetCore.Diagnostics;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// The EXCEPTION half of the non-empty-/api-error contract (the status-code-pages branch in Program.cs is
/// the other half; it backfills bodiless statuses and structurally cannot see exceptions, which write their
/// own body). Wired as the /api branch's <see cref="ExceptionHandlerOptions.ExceptionHandler"/> in every
/// environment: in Development a failed <c>[FromBody]</c> bind THROWS (<c>ThrowOnBadRequest</c> is on) and
/// used to answer a text/plain stack trace; elsewhere a genuinely unhandled throw used to re-execute the
/// HTML /Error page onto an /api caller. Both now answer the JSON <c>{message}</c> the SPA's ApiError
/// parser expects, with nothing from the exception itself on the wire.
/// </summary>
public static class ApiExceptionResponse
{
    /// <summary>
    /// <see cref="BadHttpRequestException"/> carries the status the binder chose (400 for a missing or
    /// malformed body, 413 for an oversized one) — keep it. Anything else is an unhandled server fault.
    /// </summary>
    public static int StatusFor(Exception? error) =>
        error is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;

    public static async Task Write(HttpContext context)
    {
        var status = StatusFor(context.Features.Get<IExceptionHandlerFeature>()?.Error);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { message = ApiStatusMessages.For(status) });
    }
}
