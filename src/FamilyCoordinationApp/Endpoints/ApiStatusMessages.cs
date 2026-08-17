namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Generic <c>{ message }</c> text backfilled onto an /api error response that wrote no body of its own by the
/// <c>/api</c> <c>UseStatusCodePages</c> branch in <c>Program.cs</c>, which preserves the status code.
/// <para>These are the LAST resort, not the house style: a handler that knows why it failed should say so
/// (<c>Results.NotFound(new { message = "Chore not found." })</c>). This exists so that a route which forgets,
/// or a status raised by routing/auth before any handler runs, still cannot reach a client as a bodiless 4xx.</para>
/// <para><b>The 5xx coverage has two halves.</b> This backfill covers an EXPLICIT bodiless 5xx
/// (<c>Results.StatusCode(503)</c>). Separately, the /api <c>UseExceptionHandler</c> branch calls
/// <c>ApiExceptionResponse.Write</c>: a thrown <c>BadHttpRequestException</c> keeps its status, while any other
/// unhandled exception answers JSON 500 — never a stack trace or HTML error page, in any environment.</para>
/// </summary>
public static class ApiStatusMessages
{
    public static string For(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "The request was not valid.",
        StatusCodes.Status401Unauthorized => "Authentication is required.",
        StatusCodes.Status403Forbidden => "You do not have access to this resource.",
        StatusCodes.Status404NotFound => "Not found.",
        StatusCodes.Status405MethodNotAllowed => "That method is not allowed on this route.",
        StatusCodes.Status409Conflict => "The request conflicts with the current state.",
        StatusCodes.Status429TooManyRequests => "Too many requests — try again shortly.",
        >= 500 => "Something went wrong on our end.",
        _ => "The request could not be completed.",
    };
}
