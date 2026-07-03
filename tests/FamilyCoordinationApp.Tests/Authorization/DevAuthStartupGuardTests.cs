using FamilyCoordinationApp.Authorization;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;

namespace FamilyCoordinationApp.Tests.Authorization;

/// <summary>
/// WP-03 unit tests for <see cref="DevAuthStartupGuard"/> (de-Blazor): the fail-closed startup guard that throws
/// if <c>DEV_AUTH_BYPASS</c> is truthy in a non-Development environment. This is the second lock behind the
/// IsDevelopment()-gated middleware registration — a mis-scoped deploy variable must stop the app at startup,
/// never leak an auth bypass into production.
/// </summary>
public sealed class DevAuthStartupGuardTests
{
    private static IConfiguration Config(bool? optIn)
    {
        var dict = new Dictionary<string, string?>();
        if (optIn is { } v)
        {
            dict[DevAuthStartupGuard.OptInKey] = v ? "true" : "false";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IHostEnvironment Env(string name)
    {
        var mock = new Mock<IHostEnvironment>();
        mock.SetupGet(e => e.EnvironmentName).Returns(name);
        return mock.Object;
    }

    [Fact]
    public void OptInOutsideDevelopment_Throws()
    {
        var act = () => DevAuthStartupGuard.ThrowIfEnabledOutsideDevelopment(Config(optIn: true), Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Development-only*");
    }

    [Fact]
    public void OptInInDevelopment_DoesNotThrow()
    {
        var act = () => DevAuthStartupGuard.ThrowIfEnabledOutsideDevelopment(Config(optIn: true), Env("Development"));

        act.Should().NotThrow();
    }

    [Fact]
    public void NoOptIn_OutsideDevelopment_DoesNotThrow()
    {
        var act = () => DevAuthStartupGuard.ThrowIfEnabledOutsideDevelopment(Config(optIn: false), Env("Production"));

        act.Should().NotThrow();
    }

    [Fact]
    public void NoOptInKeyAtAll_OutsideDevelopment_DoesNotThrow()
    {
        var act = () => DevAuthStartupGuard.ThrowIfEnabledOutsideDevelopment(Config(optIn: null), Env("Production"));

        act.Should().NotThrow();
    }
}
