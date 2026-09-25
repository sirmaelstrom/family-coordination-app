using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyCoordinationApp.Tests.Tenancy;

/// <summary>
/// The <see cref="TenantContext"/> state machine (fca-household-scope WP-01 item 1; council round 1). One case per
/// rule, plus the D15 OutOfRequest condition with and without an HttpContext.
/// </summary>
public class TenantContextTests
{
    private static TenantContext Create(HttpContext? httpContext = null, OutOfRequestMode mode = OutOfRequestMode.Throw) =>
        new(new HttpContextAccessor { HttpContext = httpContext }, Options.Create(new TenancyOptions { OutOfRequest = mode }));

    private static (TenantState State, int? HouseholdId, int? UserId) Snapshot(ITenantContext t) =>
        (t.State, t.HouseholdId, t.UserId);

    [Fact]
    public void A_new_context_starts_Unset_with_no_household_or_user()
    {
        var tenant = Create(new DefaultHttpContext());

        Snapshot(tenant).Should().Be((TenantState.Unset, (int?)null, (int?)null));
        tenant.IsUnfiltered.Should().BeFalse();
        tenant.IsCrossTenantWriteAllowed.Should().BeFalse();
    }

    // ── SetCaller ────────────────────────────────────────────────────────────

    [Fact]
    public void SetCaller_from_Unset_yields_Caller()
    {
        var tenant = Create(new DefaultHttpContext());

        tenant.SetCaller(1, 7);

        Snapshot(tenant).Should().Be((TenantState.Caller, (int?)1, (int?)7));
    }

    [Fact]
    public void SetCaller_from_Caller_throws()
    {
        var tenant = Create(new DefaultHttpContext());
        tenant.SetCaller(1, 7);

        var act = () => tenant.SetCaller(2, 8);

        act.Should().Throw<InvalidOperationException>();
        Snapshot(tenant).Should().Be((TenantState.Caller, (int?)1, (int?)7), "a refused SetCaller changes nothing");
    }

    [Fact]
    public void SetCaller_from_System_throws()
    {
        var tenant = Create(new DefaultHttpContext());
        using var _ = tenant.RunAs(3);

        var act = () => tenant.SetCaller(1, 7);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SetCaller_on_an_Unfiltered_instance_throws()
    {
        var tenant = TenantContext.CreateUnfiltered();

        var act = () => tenant.SetCaller(1, 7);

        act.Should().Throw<InvalidOperationException>();
        tenant.State.Should().Be(TenantState.Unfiltered);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(1, 0)]
    [InlineData(-1, 7)]
    public void SetCaller_with_a_non_positive_id_throws(int householdId, int userId)
    {
        var tenant = Create(new DefaultHttpContext());

        var act = () => tenant.SetCaller(householdId, userId);

        act.Should().Throw<ArgumentOutOfRangeException>();
        tenant.State.Should().Be(TenantState.Unset);
    }

    // ── RunAs ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RunAs_with_a_non_positive_household_throws(int householdId)
    {
        var tenant = Create(new DefaultHttpContext());

        var act = () => tenant.RunAs(householdId);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RunAs_from_Unset_yields_System_and_dispose_restores_Unset()
    {
        var tenant = Create(new DefaultHttpContext());

        using (tenant.RunAs(2))
        {
            Snapshot(tenant).Should().Be((TenantState.System, (int?)2, (int?)null));
        }

        Snapshot(tenant).Should().Be((TenantState.Unset, (int?)null, (int?)null));
    }

    [Fact]
    public void RunAs_inside_Caller_yields_System_with_no_user_and_dispose_restores_the_exact_Caller()
    {
        // V1 unit: RunAs(2) inside Caller(1,1) → System(2); dispose → Caller(1,1), UserId included.
        var tenant = Create(new DefaultHttpContext());
        tenant.SetCaller(1, 1);

        using (tenant.RunAs(2))
        {
            Snapshot(tenant).Should().Be((TenantState.System, (int?)2, (int?)null));
        }

        Snapshot(tenant).Should().Be((TenantState.Caller, (int?)1, (int?)1));
    }

    [Fact]
    public void Nested_RunAs_restores_each_level_in_turn()
    {
        var tenant = Create(new DefaultHttpContext());
        tenant.SetCaller(1, 1);

        using (tenant.RunAs(2))
        {
            using (tenant.RunAs(3))
            {
                Snapshot(tenant).Should().Be((TenantState.System, (int?)3, (int?)null));
            }
            Snapshot(tenant).Should().Be((TenantState.System, (int?)2, (int?)null));
        }

        Snapshot(tenant).Should().Be((TenantState.Caller, (int?)1, (int?)1));
    }

    [Fact]
    public void RunAs_on_an_Unfiltered_instance_is_a_no_op()
    {
        var tenant = TenantContext.CreateUnfiltered();

        using (tenant.RunAs(2))
        {
            tenant.State.Should().Be(TenantState.Unfiltered);
            tenant.HouseholdId.Should().BeNull();
            tenant.IsUnfiltered.Should().BeTrue();
        }

        tenant.State.Should().Be(TenantState.Unfiltered);
    }

    // ── AllowCrossTenantWrite ────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AllowCrossTenantWrite_requires_a_non_blank_reason(string? reason)
    {
        var tenant = Create(new DefaultHttpContext());

        var act = () => tenant.AllowCrossTenantWrite(reason!);

        act.Should().Throw<ArgumentException>();
        tenant.IsCrossTenantWriteAllowed.Should().BeFalse();
    }

    [Fact]
    public void AllowCrossTenantWrite_nests_as_a_depth_counter()
    {
        var tenant = Create(new DefaultHttpContext());

        using (tenant.AllowCrossTenantWrite("outer"))
        {
            tenant.IsCrossTenantWriteAllowed.Should().BeTrue();
            using (tenant.AllowCrossTenantWrite("inner"))
            {
                tenant.IsCrossTenantWriteAllowed.Should().BeTrue();
            }
            tenant.IsCrossTenantWriteAllowed.Should().BeTrue("the outer scope is still open");
        }

        tenant.IsCrossTenantWriteAllowed.Should().BeFalse();
    }

    [Fact]
    public void AllowCrossTenantWrite_leaves_the_tenant_state_alone()
    {
        var tenant = Create(new DefaultHttpContext());
        tenant.SetCaller(1, 1);

        using (tenant.AllowCrossTenantWrite("invite accept"))
        {
            Snapshot(tenant).Should().Be((TenantState.Caller, (int?)1, (int?)1));
        }
    }

    // ── Dispose order ────────────────────────────────────────────────────────

    [Fact]
    public void Disposing_RunAs_scopes_out_of_LIFO_order_throws_and_changes_nothing()
    {
        var tenant = Create(new DefaultHttpContext());
        tenant.SetCaller(1, 1);
        var outer = tenant.RunAs(2);
        var inner = tenant.RunAs(3);

        var act = () => outer.Dispose();

        act.Should().Throw<InvalidOperationException>();
        Snapshot(tenant).Should().Be((TenantState.System, (int?)3, (int?)null), "a refused dispose restores nothing");

        inner.Dispose();
        outer.Dispose();
        Snapshot(tenant).Should().Be((TenantState.Caller, (int?)1, (int?)1));
    }

    [Fact]
    public void Disposing_across_scope_kinds_out_of_LIFO_order_throws()
    {
        // RunAs and AllowCrossTenantWrite share one stack: interleaving them out of order is refused too.
        var tenant = Create(new DefaultHttpContext());
        var runAs = tenant.RunAs(2);
        var crossWrite = tenant.AllowCrossTenantWrite("reason");

        var act = () => runAs.Dispose();

        act.Should().Throw<InvalidOperationException>();
        tenant.IsCrossTenantWriteAllowed.Should().BeTrue();

        crossWrite.Dispose();
        runAs.Dispose();
        tenant.State.Should().Be(TenantState.Unset);
    }

    [Fact]
    public void Double_dispose_is_a_no_op()
    {
        var tenant = Create(new DefaultHttpContext());
        tenant.SetCaller(1, 1);
        var outer = tenant.RunAs(2);
        var inner = tenant.RunAs(3);

        inner.Dispose();
        var again = () => inner.Dispose();

        again.Should().NotThrow();
        Snapshot(tenant).Should().Be((TenantState.System, (int?)2, (int?)null), "the second dispose must not pop the outer scope");

        outer.Dispose();
        outer.Dispose();
        Snapshot(tenant).Should().Be((TenantState.Caller, (int?)1, (int?)1));

        var crossWrite = tenant.AllowCrossTenantWrite("reason");
        crossWrite.Dispose();
        crossWrite.Dispose();
        tenant.IsCrossTenantWriteAllowed.Should().BeFalse("a second dispose must not drive the depth negative");
        using (tenant.AllowCrossTenantWrite("fresh"))
        {
            tenant.IsCrossTenantWriteAllowed.Should().BeTrue();
        }
    }

    // ── Unfiltered (D12) ─────────────────────────────────────────────────────

    [Fact]
    public void CreateUnfiltered_is_permanently_Unfiltered_with_default_options()
    {
        var tenant = TenantContext.CreateUnfiltered();

        tenant.State.Should().Be(TenantState.Unfiltered);
        tenant.IsUnfiltered.Should().BeTrue();
        tenant.HouseholdId.Should().BeNull();
        tenant.UserId.Should().BeNull();
        tenant.IsOutOfRequest.Should().BeFalse("OutOfRequest is an Unset condition, and this instance is never Unset");
        tenant.OutOfRequestMode.Should().Be(OutOfRequestMode.Throw);
    }

    // ── OutOfRequest (D15) ───────────────────────────────────────────────────

    [Fact]
    public void Unset_with_no_HttpContext_is_OutOfRequest()
    {
        Create(httpContext: null).IsOutOfRequest.Should().BeTrue();
    }

    [Fact]
    public void Unset_with_an_HttpContext_is_not_OutOfRequest()
    {
        Create(new DefaultHttpContext()).IsOutOfRequest.Should().BeFalse();
    }

    [Fact]
    public void A_set_tenant_is_never_OutOfRequest_even_with_no_HttpContext()
    {
        var tenant = Create(httpContext: null);
        using (tenant.RunAs(2))
        {
            tenant.IsOutOfRequest.Should().BeFalse("RunAs sets System; OutOfRequest is Unset-only");
        }
        tenant.IsOutOfRequest.Should().BeTrue("dispose restores Unset");
    }

    [Theory]
    [InlineData(OutOfRequestMode.Throw)]
    [InlineData(OutOfRequestMode.Unfiltered)]
    public void OutOfRequestMode_comes_from_the_options(OutOfRequestMode mode)
    {
        Create(httpContext: null, mode: mode).OutOfRequestMode.Should().Be(mode);
    }

    [Fact]
    public void TenancyOptions_default_to_enforcing()
    {
        // Constraints M7: every default is the enforcing value.
        var options = new TenancyOptions();

        options.EnforceFilter.Should().BeTrue();
        options.EnforceWrites.Should().BeTrue();
        options.OutOfRequest.Should().Be(OutOfRequestMode.Throw);
    }

    [Fact]
    public void Each_context_gets_its_own_copy_of_the_TenancyOptions()
    {
        // PR #120 review 1 (opus): contexts used to share the cached IOptions value, so one context's mutation
        // reached every other context and the source.
        var source = Options.Create(new TenancyOptions());
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"tenancy-options-{Guid.NewGuid()}")
            .Options;
        var factory = new TenantDbContextFactory(dbOptions, Create(new DefaultHttpContext()), source, TimeProvider.System);
        using var a = factory.CreateDbContext();
        using var b = factory.CreateDbContext();

        a.Tenancy.EnforceFilter = false;

        b.Tenancy.EnforceFilter.Should().BeTrue("another context's copy is untouched");
        source.Value.EnforceFilter.Should().BeTrue("the source options are untouched");

        source.Value.EnforceWrites = false;
        source.Value.OutOfRequest = OutOfRequestMode.Unfiltered;

        a.Tenancy.EnforceWrites.Should().BeTrue("a context's copy is fixed at creation");
        a.Tenancy.OutOfRequest.Should().Be(OutOfRequestMode.Throw);
    }
}
