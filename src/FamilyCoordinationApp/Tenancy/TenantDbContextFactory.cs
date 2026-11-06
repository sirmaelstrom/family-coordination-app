using FamilyCoordinationApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// The app's <see cref="IDbContextFactory{TContext}"/>, registered <b>Scoped</b> (fca-household-scope D2): every
/// context it creates carries the scope's <see cref="ITenantContext"/>. The files that inject
/// <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c> are unchanged; they now receive this.
/// <para>The constructor shape is fixed by WP-01: WP-02 builds one by hand in a test and WP-03 reads
/// <c>Clock</c>. This is the ONE file in <c>src</c> allowed to write <c>new ApplicationDbContext(</c>
/// (architecture guard fact 3).</para>
/// <para>While the scope's <see cref="AgentUnitOfWork"/> is active (an agent write, quest 8bfa42cf), every context
/// is built on its open connection and enlisted in its transaction. The unit of work is optional, so a hand-built
/// factory without one behaves exactly as before.</para>
/// </summary>
public sealed class TenantDbContextFactory(
    DbContextOptions<ApplicationDbContext> options,
    ITenantContext tenant,
    IOptions<TenancyOptions> tenancyOptions,
    TimeProvider timeProvider,
    AgentUnitOfWork? unitOfWork = null) : IDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext()
    {
        var context = new ApplicationDbContext(options, tenant, tenancyOptions.Value) { Clock = timeProvider };
        if (unitOfWork is { Connection: { } connection, Transaction: { } transaction })
        {
            // The unit of work owns the connection: disposing this context must not close it.
            context.Database.SetDbConnection(connection, contextOwnsConnection: false);
            context.Database.UseTransaction(transaction);
        }
        return context;
    }

    public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
