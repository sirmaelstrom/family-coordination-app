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

    /// <summary>The Npgsql connection string for the running throwaway database.</summary>
    public string ConnectionString => _container.GetConnectionString();

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
