using Npgsql;
using Testcontainers.PostgreSql;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Spins up a throwaway PostgreSQL container (Testcontainers) for the chore integration suite and exposes its
/// connection string. This is the real database the InMemory provider cannot stand in for: it has the
/// PostgreSQL <c>xmin</c> system column that <see cref="FamilyCoordinationApp.Data.Entities.Chore.Version"/>
/// maps to, so the optimistic-concurrency (409) race in <see cref="ChoreConcurrencyTests"/> exercises the
/// actual production save path against a real engine.
/// <para>Shared across the whole integration test collection (one container per run, started once) via the
/// xUnit <see cref="ICollectionFixture{TFixture}"/> mechanism — see <see cref="IntegrationCollection"/>.</para>
/// <para><b>Requires Docker.</b> All tests in this collection carry <c>[Trait("kind","integration")]</c>, so
/// the fast unit suite (<c>dotnet test --filter "kind!=integration"</c>) never loads this fixture and never
/// touches Docker.</para>
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        // Pin a known-good Postgres image; xmin behavior is identical across modern majors.
        .WithImage("postgres:16-alpine")
        .WithDatabase("family_test")
        .WithUsername("family_test")
        .WithPassword("family_test_pw")
        .Build();

    /// <summary>
    /// The Npgsql connection string for the running throwaway database (the default <c>family_test</c> DB),
    /// with BOUNDED pooling applied (see <see cref="ApplyPoolLimits"/>). Per-DB strings from
    /// <see cref="CreateDatabaseConnectionStringAsync"/> inherit these limits (they are built from this string).
    /// </summary>
    public string ConnectionString => ApplyPoolLimits(_container.GetConnectionString());

    /// <summary>
    /// Cap pool size and prune idle connections aggressively. The whole integration collection shares ONE
    /// container, but each test class boots its own WebApplicationFactory against its own database — i.e. its
    /// own Npgsql pool keyed by the connection string. With the defaults (100 max per pool, 300s idle lifetime)
    /// the per-class pools accumulate idle connections across the suite and can blow past Postgres
    /// <c>max_connections</c> (100) → <c>53300: sorry, too many clients already</c>. A small per-pool cap plus
    /// fast idle pruning keeps the suite well under the cap (tests are sequential + low-concurrency, so a small
    /// pool is ample — the 2-writer concurrency tests need only a couple of connections). Pruning only ever
    /// closes IDLE connections, never in-use ones, so it cannot affect a running operation.
    /// </summary>
    private static string ApplyPoolLimits(string raw) =>
        new NpgsqlConnectionStringBuilder(raw)
        {
            MaxPoolSize = 10,
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1,
        }.ConnectionString;

    /// <summary>
    /// Create a brand-new, uniquely-named database on the shared container and return its connection string.
    /// <para>Each integration test class that materializes a schema (an HTTP <see cref="ChoresWebAppFactory"/>
    /// via <c>MigrateAsync</c>, or the service-level test via <c>EnsureCreated</c>) MUST run against its own DB:
    /// the container is shared across the collection, but a single database cannot be shared by both a
    /// <c>MigrateAsync</c>-based harness (which expects <c>__EFMigrationsHistory</c> to drive table creation) and
    /// an <c>EnsureCreated</c>-based one (which creates tables without history) — they collide with
    /// <c>42P07 relation already exists</c>. Isolating per database keeps each class's schema lifecycle
    /// independent while still using the one container (fast).</para>
    /// </summary>
    public async Task<string> CreateDatabaseConnectionStringAsync()
    {
        var dbName = $"it_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            // dbName is a server-generated GUID identifier (no user input) — safe to interpolate.
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = dbName };
        return builder.ConnectionString;
    }

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// The xUnit collection that binds every integration test class to the single shared
/// <see cref="PostgresContainerFixture"/> (one container started once, reused by all integration tests, torn
/// down at the end). Apply <c>[Collection(IntegrationCollection.Name)]</c> to each integration test class.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<PostgresContainerFixture>
{
    public const string Name = "chores-integration";
}
