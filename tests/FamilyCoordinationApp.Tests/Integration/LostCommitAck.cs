using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Fault injection for "the database committed, the client never heard" (quest c001677c). While armed, the next
/// explicit transaction commit really commits on Postgres and then throws, the way a connection that drops between
/// COMMIT and its answer does. One shot: it disarms itself, so the retry that follows commits normally.
/// </summary>
public sealed class LostCommitAck : DbTransactionInterceptor
{
    private int _armed;

    /// <summary>How many commits it has failed after they landed.</summary>
    public int Fired { get; private set; }

    public void Arm() => Interlocked.Exchange(ref _armed, 1);

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            Fired++;
            throw new IOException("Injected fault: the connection dropped after COMMIT was sent, before its answer arrived.");
        }
        return Task.CompletedTask;
    }
}
