using FamilyCoordinationApp.Services.Digest;

namespace FamilyCoordinationApp.Tests.Fakes;

/// <summary>
/// In-memory capturing fake implementation of <see cref="IDigestSender"/>. Records every invocation for
/// assertion in the WP-08 integration tests and NEVER makes a network call (no real discord.com hit).
/// <para>
/// Thread-safe: the concurrent-double-fire test (<c>DigestRunIntegrationTests</c>) issues two
/// <c>POST /api/chores/digest/run</c> calls in parallel against the same fixed instant, so two scoped
/// <see cref="DigestService"/> instances may invoke this singleton concurrently. Recording is guarded by a
/// lock; the load-bearing assertion is the recorded send count per webhook URL.
/// </para>
/// <para>
/// Failure injection: set <see cref="ThrowForUrl"/> to a predicate over the (plaintext) webhook URL to make
/// the sender throw for ONE household while the other still sends — proving failure isolation (M10). The
/// throwing invocation is NOT recorded as a successful send (it is recorded separately in
/// <see cref="ThrownUrls"/>). The boundary only sees <c>(webhookUrl, model)</c>, never a HouseholdId — so the
/// failing household must be distinguished by its seeded webhook URL or its model headline (council).
/// </para>
/// </summary>
public sealed class FakeDigestSender : IDigestSender
{
    private readonly object _gate = new();
    private readonly List<(string WebhookUrl, DigestModel Model)> _invocations = [];
    private readonly List<string> _thrownUrls = [];

    /// <summary>
    /// When set, the sender throws an <see cref="InvalidOperationException"/> for any webhook URL the
    /// predicate matches (simulating a delivery failure for that household). Default: never throws.
    /// </summary>
    public Func<string, bool>? ThrowForUrl { get; set; }

    /// <summary>All successfully-recorded (webhookUrl, model) pairs, in (lock-serialized) call order.</summary>
    public IReadOnlyList<(string WebhookUrl, DigestModel Model)> Invocations
    {
        get { lock (_gate) { return _invocations.ToList(); } }
    }

    /// <summary>Webhook URLs for which the sender was asked to deliver but threw (failure-injection).</summary>
    public IReadOnlyList<string> ThrownUrls
    {
        get { lock (_gate) { return _thrownUrls.ToList(); } }
    }

    /// <summary>Count of successful recorded sends whose webhook URL matches <paramref name="predicate"/>.</summary>
    public int SendCountWhere(Func<string, bool> predicate)
    {
        lock (_gate) { return _invocations.Count(i => predicate(i.WebhookUrl)); }
    }

    /// <inheritdoc/>
    public Task SendAsync(string webhookUrl, DigestModel model, CancellationToken ct = default)
    {
        if (ThrowForUrl is { } shouldThrow && shouldThrow(webhookUrl))
        {
            lock (_gate) { _thrownUrls.Add(webhookUrl); }
            throw new InvalidOperationException("Simulated digest delivery failure (failure-isolation test).");
        }

        lock (_gate) { _invocations.Add((webhookUrl, model)); }
        return Task.CompletedTask;
    }
}
