using FamilyCoordinationApp.Services;
using Microsoft.AspNetCore.Diagnostics;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// The EXCEPTION half of the non-empty-/api-error contract (the status-code-pages branch in Program.cs is
/// the other half; it backfills bodiless statuses and structurally cannot see exceptions, which write their
/// own body). Wired as the /api branch's <see cref="ExceptionHandlerOptions.ExceptionHandler"/> in every
/// environment: in Development a failed <c>[FromBody]</c> bind THROWS (<c>ThrowOnBadRequest</c> is on) and
/// used to answer a text/plain stack trace; elsewhere a genuinely unhandled throw used to re-execute the
/// HTML /Error page onto an /api caller. Both now answer the JSON <c>{message}</c> the SPA's ApiError
/// parser expects.
/// <para><b>The one exception → status map for /api</b> (quest 940b4022). A <see cref="DomainException"/> answers its
/// own status with its <see cref="DomainException.ClientMessage"/>; endpoints do not catch them. Anything else is a
/// server fault: 500 with a generic message, nothing from the exception on the wire. That includes the BCL
/// <see cref="InvalidOperationException"/>/<see cref="KeyNotFoundException"/>/<see cref="ArgumentException"/>
/// that services used to throw for not-found/invalid, and that EF throws for its own faults.</para>
/// <para>A delegate on the branch, not a registered <c>IExceptionHandler</c>: registered handlers are also consulted
/// by the page pipeline's <c>UseExceptionHandler("/Error")</c>, and Razor Pages keep their own handling.</para>
/// </summary>
public static class ApiExceptionResponse
{
    /// <summary>
    /// <see cref="BadHttpRequestException"/> carries the status the binder chose (400 for a missing or
    /// malformed body, 413 for an oversized one) — keep it. A <see cref="DomainException"/> carries its own.
    /// Anything else is an unhandled server fault.
    /// </summary>
    public static int StatusFor(Exception? error) => error switch
    {
        BadHttpRequestException bad => bad.StatusCode,
        DomainException domain => domain.StatusCode,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>The body text: a domain refusal's own client message, else the generic text for the status.</summary>
    public static string MessageFor(Exception? error, int status) =>
        error is DomainException domain ? domain.ClientMessage : ApiStatusMessages.For(status);

    /// <summary>
    /// The branch's <see cref="ExceptionHandlerOptions.SuppressDiagnosticsCallback"/>: a domain refusal is an
    /// expected outcome, not an unhandled exception, so it is not logged at Error (the endpoint catches it replaced
    /// never logged either). Every other exception keeps the middleware's Error log line.
    /// </summary>
    public static bool IsExpected(ExceptionHandlerSuppressDiagnosticsContext context) => context.Exception is DomainException;

    public static async Task Write(HttpContext context)
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var status = StatusFor(error);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { message = MessageFor(error, status) });
    }
}
