using Microsoft.AspNetCore.Hosting;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The test hosts' tenancy setting (fca-household-scope D15). About 13 integration test files, and
/// <see cref="ChoresWebAppFactory"/>'s own seed step, resolve <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c>
/// from the host's ROOT provider, outside any HTTP request, to arrange and assert data. <c>Unfiltered</c> keeps that
/// working once WP-02 turns the read filter on. Inside a request an Unset tenant still throws whatever this says,
/// so a forgotten <c>RequireTenant()</c> is still caught.
/// <para>Every root <c>WebApplicationFactory&lt;Program&gt;</c> calls this in its <c>ConfigureWebHost</c>; a
/// source-scan fact in <c>TenantPlumbingTests</c> fails any root host that doesn't. <c>UseSetting</c> is the house
/// pattern, and a later <c>UseSetting</c> (for example <c>"Throw"</c> in a <c>WithWebHostBuilder</c>) overrides it.
/// Production never sets this (constraints M7).</para>
/// </summary>
public static class TestHostTenancy
{
    public static void Apply(IWebHostBuilder builder) => builder.UseSetting("Tenancy:OutOfRequest", "Unfiltered");
}
