using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using FamilyCoordinationApp.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FamilyCoordinationApp.Endpoints.Mcp;

/// <summary>
/// The one place agent tool calls are authorized, limited and audited (quest 8bfa42cf, design §5–6):
/// <list type="bullet">
/// <item><b>Allowlist</b>: a tool missing from <see cref="Tools"/> is refused, so a tool added later is denied until
/// it is listed here with its scope.</item>
/// <item><b>Scope</b>: a write tool needs the token's <c>family.write</c> scope; a read token is refused whatever
/// arguments it sends. <c>tools/list</c> hides the write tools from a read token.</item>
/// <item><b>Rate</b>: per token, <see cref="CallsPerMinute"/> calls, <see cref="WritesPerMinute"/> writes and
/// <see cref="FetchesPerMinute"/> outbound fetches a minute.</item>
/// <item><b>Audit</b>: one log line per call with the token id, member, household, tool, request id, outcome and
/// duration. Never arguments' content, never the token.</item>
/// </list>
/// Authentication itself happened on this HTTP request (the bearer policy on <c>/mcp</c>), so a revoked token never
/// reaches here.
/// </summary>
public static class AgentToolGate
{
    public enum Access { Read, Write }

    public const int CallsPerMinute = 120;
    public const int WritesPerMinute = 30;
    public const int FetchesPerMinute = 10;

    public static readonly IReadOnlyDictionary<string, Access> Tools = new Dictionary<string, Access>(StringComparer.Ordinal)
    {
        ["search_recipes"] = Access.Read,
        ["get_recipe"] = Access.Read,
        ["preview_recipe_import"] = Access.Read,
        ["get_meal_plan"] = Access.Read,
        ["get_shopping_lists"] = Access.Read,
        ["create_recipe"] = Access.Write,
        ["add_meal"] = Access.Write,
        ["set_meal_servings"] = Access.Write,
        ["generate_shopping_list"] = Access.Write,
        ["add_shopping_item"] = Access.Write,
    };

    private static readonly ConcurrentDictionary<(int TokenId, string Bucket), Window> Windows = new();

    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> FilterList(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (request, ct) =>
        {
            var result = await next(request, ct);
            var canWrite = Http(request.Services)?.User.HasClaim(AgentAccess.ScopeClaim, AgentAccess.WriteScope) == true;
            result.Tools = result.Tools
                .Where(tool => Tools.TryGetValue(tool.Name, out var access) && (access == Access.Read || canWrite))
                .ToList();
            return result;
        };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> FilterCall(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, ct) =>
        {
            var http = Http(request.Services);
            var logger = request.Services?.GetService<ILoggerFactory>()?.CreateLogger("FamilyCoordinationApp.AgentTools");
            var identity = http is null ? null : AgentAccess.ReadIdentity(http.User);
            var tool = request.Params?.Name ?? string.Empty;
            var requestId = request.Params?.Arguments is { } args && args.TryGetValue("requestId", out var rid) ? rid.ToString() : null;
            var correlationId = http?.TraceIdentifier ?? Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
            var started = Stopwatch.GetTimestamp();

            CallToolResult result;
            if (identity is not { } caller)
            {
                // Unreachable through the /mcp policy; fail closed if it ever is.
                result = ToolResults.Error(tool, "unauthenticated", "No agent token identity on this request.", correlationId);
            }
            else if (!Tools.TryGetValue(tool, out var access))
            {
                result = ToolResults.Error(tool, "unknown_tool", $"There is no tool named \"{tool}\" for agent tokens.", correlationId);
            }
            else if (access == Access.Write && !http!.User.HasClaim(AgentAccess.ScopeClaim, AgentAccess.WriteScope))
            {
                result = ToolResults.Error(tool, "forbidden",
                    "This agent token is read-only, so nothing was changed. A member can create a read-and-write token in Settings → Agent access.",
                    correlationId);
            }
            else if (UnknownArguments(request) is { Count: > 0 } extra)
            {
                result = ToolResults.Error(tool, "invalid_argument",
                    $"unknown argument(s) {string.Join(", ", extra.Select(e => $"\"{e}\""))}. Nothing was run; send only the tool's declared arguments.",
                    correlationId);
            }
            else if (Limited(caller.TokenId, tool, IsDryRun(request) ? Access.Read : access,
                         (request.Services?.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime) is { } limit)
            {
                result = ToolResults.Error(tool, "rate_limited",
                    $"Too many {limit} for this token in the last minute, so nothing was run. Wait a minute and retry with the same requestId.",
                    correlationId);
            }
            else
            {
                result = await next(request, ct);
            }

            logger?.LogInformation(
                "Agent tool {Tool} by token {TokenId} (user {UserId}, household {HouseholdId}) request {RequestId}: {Outcome} in {ElapsedMs} ms [{CorrelationId}]",
                tool, identity?.TokenId, identity?.UserId, identity?.HouseholdId, requestId,
                result.IsError == true ? ErrorCode(result) : "ok",
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, correlationId);
            return result;
        };

    /// <summary>Argument names the tool's input schema does not declare (schemas reject extra fields).</summary>
    private static List<string> UnknownArguments(RequestContext<CallToolRequestParams> request)
    {
        if (request.Params?.Arguments is not { Count: > 0 } arguments) return [];
        McpServerTool? tool = request.MatchedPrimitive as McpServerTool;
        if (tool is null && request.Server.ServerOptions.ToolCollection is { } tools)
        {
            tools.TryGetPrimitive(request.Params.Name, out tool);
        }
        if (tool is null) return [];
        var schema = tool.ProtocolTool.InputSchema;
        var declared = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == System.Text.Json.JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        return arguments.Keys.Where(name => !declared.Contains(name)).ToList();
    }

    /// <summary>
    /// A dry run persists nothing, so it counts against the call budget only, not the write budget. Measured in the
    /// real-client acceptance run: both agents previewed every change, and a week's plan (1 recipe, 7 meals,
    /// 7 servings, 1 list, each previewed then saved) needs about 32 write calls in a minute.
    /// </summary>
    private static bool IsDryRun(RequestContext<CallToolRequestParams> request) =>
        request.Params?.Arguments is { } args
        && args.TryGetValue("dryRun", out var dryRun)
        && dryRun.ValueKind == System.Text.Json.JsonValueKind.True;

    /// <summary>The bucket that is over its limit, or null. Counts this call when it is allowed.</summary>
    private static string? Limited(int tokenId, string tool, Access access, DateTime now)
    {
        if (!Take(tokenId, "calls", CallsPerMinute, now)) return "calls";
        if (access == Access.Write && !Take(tokenId, "writes", WritesPerMinute, now)) return "writes";
        if (tool == "preview_recipe_import" && !Take(tokenId, "fetches", FetchesPerMinute, now)) return "page fetches";
        return null;
    }

    private static bool Take(int tokenId, string bucket, int limit, DateTime now)
    {
        var window = Windows.GetOrAdd((tokenId, bucket), _ => new Window());
        lock (window)
        {
            if (now - window.Start >= TimeSpan.FromMinutes(1))
            {
                window.Start = now;
                window.Count = 0;
            }
            if (window.Count >= limit) return false;
            window.Count++;
            return true;
        }
    }

    /// <summary>Test seam: forget every rate window.</summary>
    internal static void ResetLimits() => Windows.Clear();

    private static string ErrorCode(CallToolResult result) =>
        result.StructuredContent is { } element
        && element.ValueKind == System.Text.Json.JsonValueKind.Object
        && element.TryGetProperty("error", out var error)
        && error.TryGetProperty("code", out var code)
            ? code.GetString() ?? "error"
            : "error";

    private static HttpContext? Http(IServiceProvider? services) =>
        services?.GetService<IHttpContextAccessor>()?.HttpContext;

    private sealed class Window
    {
        public DateTime Start = DateTime.MinValue;
        public int Count;
    }
}
