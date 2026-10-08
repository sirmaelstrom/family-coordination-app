using System.ComponentModel.DataAnnotations;
using FamilyCoordinationApp.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Request validation for <c>/api</c> (quest ec7a7331). <c>AddValidation()</c> (Program.cs) runs the attributes on
/// every request record a minimal-API endpoint binds, before the handler. A request string longer than its column
/// is refused with a 400 instead of reaching Postgres as a 22001 and answering 500. The limits come from
/// <c>Data/FieldLengths</c>, the same constants the EF configurations declare the columns from.
/// <para>The SPA shows a 400's <c>message</c> and otherwise the raw body text (<c>messageFrom</c>, client.ts), so a
/// validation failure must carry a <c>message</c>. The built-in filter writes an <c>HttpValidationProblemDetails</c>
/// through <see cref="IProblemDetailsService"/>; <see cref="ApiProblemDetailsWriter"/> is the writer it gets on
/// <c>/api</c>, and it adds the first error's text as <c>message</c> next to <c>errors</c>.</para>
/// </summary>
public static class ApiValidation
{
    /// <summary>Message templates. <c>{0}</c> is the field's <c>[Display(Name)]</c>; <c>{1}</c> is the limit.</summary>
    public const string RequiredMessage = "{0} is required.";

    public const string TooLongMessage = "{0} must be {1} characters or fewer.";

    /// <summary>
    /// The <c>message</c> for a problem body: the first validation error when there is one, else the problem's
    /// own <c>detail</c>, else the generic text the /api status backfill uses for that status.
    /// </summary>
    public static string MessageFor(ProblemDetails problem, int status)
    {
        if (problem is HttpValidationProblemDetails validation)
        {
            var first = validation.Errors.Values.SelectMany(messages => messages)
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
            if (first is not null) return first;
        }

        return string.IsNullOrWhiteSpace(problem.Detail) ? ApiStatusMessages.For(status) : problem.Detail;
    }
}

/// <summary><c>[Required]</c> with the house message: "Room name is required." Rejects null, empty and whitespace.</summary>
public sealed class RequiredTextAttribute : RequiredAttribute
{
    public RequiredTextAttribute() => ErrorMessage = ApiValidation.RequiredMessage;
}

/// <summary>
/// <c>[StringLength(max)]</c> with the house message: "Room name must be 100 characters or fewer." It checks the
/// annotated field against its own column's limit, on the raw value as sent (before any trimming the handler does),
/// counted in UTF-16 code units, which is stricter than Postgres <c>varchar</c> for supplementary characters. A value
/// the handler or service DERIVES from the field (a display name taken from an email) is outside it and needs its
/// own check.
/// </summary>
public sealed class MaxTextLengthAttribute : StringLengthAttribute
{
    public MaxTextLengthAttribute(int maximumLength) : base(maximumLength) => ErrorMessage = ApiValidation.TooLongMessage;
}

/// <summary>
/// The <see cref="IProblemDetailsWriter"/> for <c>/api</c>, registered ahead of the default writer (the
/// <see cref="IProblemDetailsService"/> asks writers in registration order). Unlike the default writer it does not
/// depend on the request's <c>Accept</c> header, so an <c>/api</c> problem body always reaches the caller as JSON, and
/// it adds the <c>message</c> the SPA reads. Its scope is the path prefix alone: EVERY problem body written through
/// the service on <c>/api</c> gets this shape, a future <c>Results.Problem</c> included, not only validation failures.
/// Non-<c>/api</c> paths fall through to the default writer.
/// </summary>
public sealed class ApiProblemDetailsWriter(IOptions<HttpJsonOptions> jsonOptions) : IProblemDetailsWriter
{
    public bool CanWrite(ProblemDetailsContext context) =>
        context.HttpContext.Request.Path.StartsWithSegments(ApiAwareAuthEvents.ApiPrefix);

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        var response = context.HttpContext.Response;
        var problem = context.ProblemDetails;
        var status = problem.Status ?? response.StatusCode;
        problem.Status = status;
        response.StatusCode = status;
        problem.Extensions.TryAdd("message", ApiValidation.MessageFor(problem, status));

        return new ValueTask(response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            jsonOptions.Value.SerializerOptions,
            contentType: "application/problem+json",
            cancellationToken: context.HttpContext.RequestAborted));
    }
}
