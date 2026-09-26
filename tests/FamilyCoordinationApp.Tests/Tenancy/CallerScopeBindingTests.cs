using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FamilyCoordinationApp.Tests.Tenancy;

/// <summary>
/// <see cref="CallerScope.BindAsync"/> once every handler takes a <see cref="CallerScope"/> (fca-household-scope
/// WP-04, V8). The binder is the handlers' only source of the caller, so it must refuse every tenant state but
/// Caller: a handler reached without <c>RequireTenant()</c> is a loud 500, never a run with no household.
/// </summary>
public sealed class CallerScopeBindingTests
{
    private static (DefaultHttpContext Http, TenantContext Tenant) Arrange()
    {
        var http = new DefaultHttpContext();
        var tenant = new TenantContext(new HttpContextAccessor { HttpContext = http }, Options.Create(new TenancyOptions()));
        http.RequestServices = new ServiceCollection().AddSingleton<ITenantContext>(tenant).BuildServiceProvider();
        return (http, tenant);
    }

    [Fact]
    public async Task Binds_the_Caller_household_and_user()
    {
        var (http, tenant) = Arrange();
        tenant.SetCaller(householdId: 3, userId: 11);

        var scope = await CallerScope.BindAsync(http);

        scope.Should().Be(new CallerScope(HouseholdId: 3, UserId: 11));
    }

    [Fact]
    public async Task Throws_when_the_tenant_is_Unset()
    {
        // The forgotten-marker case: the middleware skipped the endpoint, so nothing set the tenant.
        var (http, _) = Arrange();

        var act = async () => await CallerScope.BindAsync(http);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Unset*RequireTenant()*");
    }

    [Fact]
    public async Task Throws_when_the_tenant_is_System()
    {
        // System work (RunAs) has a household but no caller. A handler must never mistake it for one.
        var (http, tenant) = Arrange();
        using var system = tenant.RunAs(2);

        var act = async () => await CallerScope.BindAsync(http);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*System*");
    }

    [Fact]
    public async Task Throws_inside_a_RunAs_opened_over_a_Caller()
    {
        // RunAs pushes System over the Caller; binding in that window must still refuse.
        var (http, tenant) = Arrange();
        tenant.SetCaller(householdId: 1, userId: 7);
        using var system = tenant.RunAs(2);

        var act = async () => await CallerScope.BindAsync(http);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
