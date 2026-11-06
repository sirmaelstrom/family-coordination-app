using System.Data.Common;

namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// One database transaction shared by every context the scope's <see cref="TenantDbContextFactory"/> creates while
/// it is open (quest 8bfa42cf). An agent write runs the existing services, each of which opens its own short-lived
/// context; enlisting them all in one connection and transaction lets the operation record (idempotency) and the
/// domain writes commit together, and lets a dry run roll every write back.
/// <para>EF creates a savepoint around each <c>SaveChanges</c> inside an existing transaction and rolls back to it on
/// failure, so <c>IKeyAllocator</c>'s retry after a unique violation still works inside the shared transaction.</para>
/// <para>Scoped: never shared between requests. Only <c>AgentCommandRunner</c> opens one.</para>
/// </summary>
public sealed class AgentUnitOfWork
{
    /// <summary>The open connection contexts must use, or <c>null</c> when no unit of work is active.</summary>
    public DbConnection? Connection { get; private set; }

    /// <summary>The open transaction contexts must enlist in, or <c>null</c> when no unit of work is active.</summary>
    public DbTransaction? Transaction { get; private set; }

    public bool IsActive => Transaction is not null;

    /// <summary>Begin. The connection and transaction stay owned by the caller, which disposes them.</summary>
    public void Begin(DbConnection connection, DbTransaction transaction)
    {
        if (IsActive) throw new InvalidOperationException("An agent unit of work is already active in this scope.");
        Connection = connection;
        Transaction = transaction;
    }

    public void End()
    {
        Connection = null;
        Transaction = null;
    }
}
