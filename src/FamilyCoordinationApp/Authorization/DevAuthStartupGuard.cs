using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FamilyCoordinationApp.Authorization;

/// <summary>
/// Fail-closed startup guard for the Development-only auth bypass (de-Blazor WP-03). The bypass middleware is
/// registered only inside an <c>if (env.IsDevelopment())</c> block; this guard is the second lock: if a
/// <c>DEV_AUTH_BYPASS</c> opt-in is ever truthy in a NON-Development environment (e.g. a mis-scoped deploy
/// variable), refuse to start rather than risk an auth bypass in production.
/// </summary>
public static class DevAuthStartupGuard
{
    /// <summary>The opt-in configuration key / environment variable checked by the guard.</summary>
    public const string OptInKey = "DEV_AUTH_BYPASS";

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if <c>DEV_AUTH_BYPASS</c> is truthy while the environment
    /// is not Development; no-op otherwise. Call this before <c>builder.Build()</c> so a misconfiguration fails
    /// at startup, never at request time.
    /// </summary>
    public static void ThrowIfEnabledOutsideDevelopment(IConfiguration configuration, IHostEnvironment environment)
    {
        var optedIn = configuration.GetValue<bool>(OptInKey);
        if (optedIn && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{OptInKey} is set but the environment is '{environment.EnvironmentName}', not Development. " +
                "The dev-auth bypass is Development-only; refusing to start (fail closed).");
        }
    }
}
