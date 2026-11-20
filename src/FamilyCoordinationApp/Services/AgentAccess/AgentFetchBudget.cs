namespace FamilyCoordinationApp.Services.AgentAccess;

/// <summary>
/// The caller's budget of outbound web fetches (a page preview, a recipe image). <see cref="AgentCommands"/> spends
/// one at the point a fetch is about to leave the server, so a call that never fetches costs none: a retry answered
/// from its operation record, and a refusal that comes first (no such recipe, an image already there, a URL the
/// fetcher would not connect to).
/// </summary>
public interface IAgentFetchBudget
{
    /// <summary>Count one outbound fetch, or throw <see cref="AgentFetchLimitException"/> when the budget is spent.</summary>
    void Spend();
}

/// <summary>
/// The fetch budget is spent, so the fetch was not made and nothing was saved. Not a <see cref="DomainException"/>:
/// it has no HTTP status of its own, and only the agent tools (<c>ToolResults</c>) answer it, as <c>rate_limited</c>.
/// </summary>
public sealed class AgentFetchLimitException() : Exception("The agent token's web fetch budget for this minute is spent.");

/// <summary>
/// The agent write was sent to the database and its COMMIT was not confirmed (a dropped connection, a request
/// cancelled mid-commit): the change may or may not be saved. Thrown only by <see cref="AgentCommandRunner"/>, and
/// only from the commit itself; every failure before it is a certain rollback and keeps its own exception.
/// A caller that made something outside the transaction (an uploaded image) must keep it: the committed row and the
/// operation record a retry replays may already name it.
/// </summary>
public sealed class AgentCommitOutcomeUnknownException(string tool, Exception innerException)
    : Exception($"The commit of agent write {tool} was not confirmed, so its outcome is unknown.", innerException);
