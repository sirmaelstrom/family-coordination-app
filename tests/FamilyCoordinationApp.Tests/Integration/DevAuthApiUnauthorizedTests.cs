using System.Net;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// WP-03 (de-Blazor) end-to-end auth-leak guard (V5): an ANONYMOUS <c>GET /api/me</c> in a non-Development
/// environment must return <b>401</b> — NOT a 302 (to the Google OAuth endpoint) and NOT a 200. This proves the
/// whole wiring composes against the REAL cookie + Google auth handlers (unlike <see cref="ChoresWebAppFactory"/>,
/// which swaps in a test scheme): the <c>DevAuthBypassMiddleware</c> is IsDevelopment()-gated (inert in
/// "Testing"), and <see cref="FamilyCoordinationApp.Authorization.ApiAwareAuthEvents"/> surfaces the /api
/// challenge as 401.
/// <para>Uses env "Testing" rather than "Production" only because Production additionally requires a
/// DATAPROTECTION_CERT at startup (Program.cs); Testing exercises the identical IsDevelopment()==false gate
/// branch without that ceremony. Requires Docker (Testcontainers Postgres) so the first-run-setup check and the
/// authorization pipeline run against a real engine.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class DevAuthApiUnauthorizedTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private DevAuthTestingWebAppFactory _factory = null!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        _factory = new DevAuthTestingWebAppFactory(connectionString);
        // Migrate + seed one household so IsSetupCompleteAsync() is true: otherwise the first-run-setup middleware
        // 302-redirects every non-skipped path (including /api/me) to /setup BEFORE authorization runs, and the
        // request would never reach the 401 path under test.
        await _factory.MigrateAndSeedAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ApiMe_Anonymous_InNonDevelopmentEnv_Returns401_Not302_Not200()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an anonymous /api request must surface as 401 for the SPA session store — not a 302 to OAuth, not a 200");
    }
}

/// <summary>
/// Boots the real <see cref="Program"/> host in the "Testing" environment (non-Development → DevAuthBypass inert;
/// non-Production → no DataProtection cert required) against a Testcontainers Postgres, KEEPING the real cookie +
/// Google auth — because WP-03's /api 401 behavior lives in those real handlers' redirect events.
/// </summary>
public sealed class DevAuthTestingWebAppFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        // Satisfy the mandatory Google OAuth config keys so Program.cs startup does not throw.
        builder.UseSetting("Authentication:Google:ClientId", "test-client-id");
        builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");
    }

    /// <summary>
    /// Migrate + seed via a standalone Npgsql context BEFORE the host serves any request, so the setup-complete
    /// check (and any startup query) sees the schema. Done standalone (not through the host's services) to keep
    /// the schema ready independent of when the host's background services first tick.
    /// </summary>
    public async Task MigrateAndSeedAsync()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.MigrateAsync();
        if (!await db.Households.AnyAsync())
        {
            db.Households.Add(new Household { Id = 1, Name = "Setup-complete household", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
    }
}
