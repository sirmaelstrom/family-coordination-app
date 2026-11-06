using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.AgentAccess;
using ModelContextProtocol.Protocol;

namespace FamilyCoordinationApp.Endpoints.Mcp;

/// <summary>
/// The shape of every agent tool result (quest 8bfa42cf, design §5 "Error standard"). Success: the data as JSON text
/// plus the same object as structured content. Failure: <c>isError: true</c>, one sentence naming the operation and
/// what was or wasn't persisted, a stable machine <c>code</c> and the request's <c>correlationId</c>. Domain failures
/// keep their client-safe message; anything unexpected says only that, and for a write that the outcome is UNKNOWN
/// and how to find out safely (retry with the same requestId), never a confident "nothing changed".
/// </summary>
public static class ToolResults
{
    /// <summary>The SDK's options (its protocol types' resolver) plus web casing, camelCase enums and reflection for our DTOs.</summary>
    public static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(ModelContextProtocol.McpJsonUtilities.DefaultOptions)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.TypeInfoResolverChain.Add(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly();
        return options;
    }

    public static CallToolResult Ok(object value)
    {
        var element = JsonSerializer.SerializeToElement(value, Json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
            IsError = false,
        };
    }

    /// <summary>A write's result, with whether it was only previewed or replayed from an earlier identical request.</summary>
    public static CallToolResult Ok<T>(AgentWriteOutcome<T> outcome) => Ok(new
    {
        dryRun = outcome.DryRun,
        replayed = outcome.Replayed,
        saved = !outcome.DryRun,
        result = outcome.Result,
    });

    public static CallToolResult Error(string tool, string code, string message, string correlationId)
    {
        var text = $"{tool} failed: {message}";
        var node = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["tool"] = tool,
                ["code"] = code,
                ["message"] = text,
                ["correlationId"] = correlationId,
            },
        };
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"{text} (code: {code}, correlationId: {correlationId})" }],
            StructuredContent = JsonSerializer.SerializeToElement(node, Json),
            IsError = true,
        };
    }

    /// <summary>Run one tool body and turn its failure into a tool error result.</summary>
    public static async Task<CallToolResult> RunAsync(
        HttpContext http, string tool, bool isWrite, Func<AgentCommands, Task<CallToolResult>> body)
    {
        var correlationId = http.TraceIdentifier;
        try
        {
            return await body(http.RequestServices.GetRequiredService<AgentCommands>());
        }
        catch (ToolArgumentException ex)
        {
            return Error(tool, "invalid_argument", $"{ex.Message} Nothing was changed.", correlationId);
        }
        catch (ConcurrencyConflictException)
        {
            return Error(tool, "stale_version",
                "the row changed after you read it (409). Nothing was changed by this request. Read it again and retry with the new version.",
                correlationId);
        }
        catch (DomainException ex)
        {
            var code = ex switch
            {
                NotFoundException => "not_found",
                DomainValidationException => "invalid_argument",
                ConflictException => "conflict",
                ForbiddenException => "forbidden",
                _ => "rejected",
            };
            var message = ex.ClientMessage;
            if (!message.Contains("nothing was", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("no recipe was saved", StringComparison.OrdinalIgnoreCase))
            {
                message += isWrite ? " Nothing was changed." : string.Empty;
            }
            return Error(tool, code, message, correlationId);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("FamilyCoordinationApp.AgentTools")
                .LogError(ex, "Agent tool {Tool} failed unexpectedly [{CorrelationId}]", tool, correlationId);
            return Error(tool, "internal_error", isWrite
                ? "an unexpected server error happened, so the outcome is unknown. Retry with the SAME requestId: it returns the committed result if the change was saved, or runs it once if it was not. Do not make a new requestId."
                : "an unexpected server error happened. Nothing was changed; try again.",
                correlationId);
        }
    }
}

/// <summary>An argument that failed the tool's own parsing (a date, an enum), before any command ran.</summary>
public sealed class ToolArgumentException(string message) : Exception(message);
