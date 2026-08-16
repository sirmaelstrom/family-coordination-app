namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Generic <c>{ message }</c> text for an /api error response that reached the pipeline with no body of its own —
/// the backfill behind the <c>/api</c> <c>UseStatusCodePages</c> branch in <c>Program.cs</c>.
/// <para>These are the LAST resort, not the house style: a handler that knows why it failed should say so
/// (<c>Results.NotFound(new { message = "Chore not found." })</c>). This exists so that a route which forgets,
/// or a status raised by routing/auth before any handler runs, still cannot reach a client as a bodiless 4xx.</para>
/// <para><b>The 5xx case is narrower than it looks.</b> It covers an EXPLICIT bodiless 5xx
/// (<c>Results.StatusCode(503)</c>). It does NOT cover an unhandled exception: <c>UseExceptionHandler("/Error")</c>
/// is registered earlier in the pipeline and writes its own response, so this branch never sees it. Making the
/// exception path answer JSON on /api is a separate change — quest <c>ea816df2</c>.</para>
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
