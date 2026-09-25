using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// Sets the request's tenant for every endpoint carrying <see cref="TenantScopedMetadata"/> (fca-household-scope
/// D3). Registered immediately after <c>UseAuthorization()</c>, so an anonymous or non-whitelisted caller has
/// already been answered by the auth pipeline before this runs.
/// <para>Resolution is the existing <see cref="UserContextResolver"/> query, run once here. Handlers still resolve
/// on their own until WP-04 collapses them onto <see cref="CallerScope"/>. An unresolvable caller gets
/// <b>401</b> with a JSON <c>{ message }</c> (constraints M5), never 400 or 403. Unmarked endpoints pass through
/// with the tenant still Unset.</para>
/// </summary>
public sealed class CallerTenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenant,
        IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<TenantScopedMetadata>() is null)
        {
            await next(context);
            return;
        }

        var caller = await UserContextResolver.ResolveUserAsync(context.User, dbFactory, context.RequestAborted);
        if (caller is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new { message = ApiStatusMessages.For(StatusCodes.Status401Unauthorized) },
                context.RequestAborted);
            return;
        }

        tenant.SetCaller(caller.HouseholdId, caller.UserId);
        await next(context);
    }
}
