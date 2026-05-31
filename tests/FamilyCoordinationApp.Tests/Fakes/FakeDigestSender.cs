using FamilyCoordinationApp.Services.Digest;

namespace FamilyCoordinationApp.Tests.Fakes;

/// <summary>
/// In-memory fake implementation of <see cref="IDigestSender"/>. Records all invocations for
/// assertion in integration tests (WP-08). Never makes a network call.
/// </summary>
public class FakeDigestSender : IDigestSender
{
    private readonly List<(string WebhookUrl, DigestModel Model)> _invocations = [];

    /// <summary>All (webhookUrl, model) pairs recorded in call order.</summary>
    public IReadOnlyList<(string WebhookUrl, DigestModel Model)> Invocations => _invocations;

    /// <inheritdoc/>
    public Task SendAsync(string webhookUrl, DigestModel model, CancellationToken ct = default)
    {
        _invocations.Add((webhookUrl, model));
        return Task.CompletedTask;
    }
}
