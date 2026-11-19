using FamilyCoordinationApp.Authorization;
using FamilyCoordinationApp.Services.AgentAccess;

namespace FamilyCoordinationApp.Endpoints.Mcp;

/// <summary>
/// The request's agent token's share of <see cref="AgentToolGate.FetchesPerMinute"/>, counted in the gate's own
/// per-token windows. The gate counts calls and writes as a call arrives; this counts a fetch when
/// <see cref="AgentCommands"/> is about to make one, which the gate cannot know from the arguments.
/// <para>Outside an HTTP request there is no token and nothing to count (the commands' own tests run that way). A
/// request without an agent identity cannot reach the commands (the <c>/mcp</c> bearer policy and the gate both
/// refuse it first), so that case fails closed.</para>
/// </summary>
public sealed class TokenFetchBudget(IHttpContextAccessor accessor, TimeProvider time) : IAgentFetchBudget
{
    public void Spend()
    {
        if (accessor.HttpContext is not { } http) return;
        var caller = AgentAccess.ReadIdentity(http.User)
            ?? throw new InvalidOperationException("An agent fetch needs the request's agent token identity.");
        if (!AgentToolGate.TakeFetch(caller.TokenId, time.GetUtcNow().UtcDateTime))
        {
            throw new AgentFetchLimitException();
        }
    }
}
