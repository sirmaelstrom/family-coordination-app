using Microsoft.Extensions.Options;

namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// The scoped <see cref="ITenantContext"/> (fca-household-scope D2). One instance per DI scope — per HTTP request,
/// or per <c>CreateScope()</c> — and never ambient: this class holds no <c>AsyncLocal</c> and no static accessor
/// (MN10).
/// <para><b>But <see cref="IsOutOfRequest"/> follows the async flow.</b> It reads
/// <c>IHttpContextAccessor.HttpContext</c>, which is <c>AsyncLocal</c>-backed. So a <c>CreateScope()</c> or
/// <c>Task.Run</c> started inside a request counts as in-request, and a continuation that outlives its request
/// counts as out-of-request (Unfiltered in the test hosts, a throw in production). WP-02 relies on this split
/// (D15).</para>
/// <para><b>State machine</b> (council round 1): <see cref="SetCaller"/> only from Unset; <see cref="RunAs"/> pushes
/// System and its dispose restores the exact previous state (Caller's <c>UserId</c> included);
/// <see cref="AllowCrossTenantWrite"/> is a depth counter. Both kinds of scope share ONE stack, so disposing any
/// scope out of LIFO order throws, and a second dispose of the same scope is a no-op. On an Unfiltered instance
/// <see cref="RunAs"/> is a no-op, so services can call <c>context.Tenant.RunAs(...)</c> unchanged under the
/// options-only test constructor.</para>
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private readonly IHttpContextAccessor? _accessor;
    private readonly Stack<Scope> _scopes = new();

    private TenantState _state;
    private int? _householdId;
    private int? _userId;
    private int _crossTenantWriteDepth;

    /// <summary>The DI constructor. Starts <see cref="TenantState.Unset"/>.</summary>
    public TenantContext(IHttpContextAccessor accessor, IOptions<TenancyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(options);
        _accessor = accessor;
        OutOfRequestMode = options.Value.OutOfRequest;
        _state = TenantState.Unset;
    }

    private TenantContext()
    {
        OutOfRequestMode = new TenancyOptions().OutOfRequest;
        _state = TenantState.Unfiltered;
    }

    /// <summary>
    /// A permanently <see cref="TenantState.Unfiltered"/> instance with default options. Called ONLY by
    /// <c>ApplicationDbContext</c>'s options-only constructor (D12); the architecture guard pins that.
    /// </summary>
    public static TenantContext CreateUnfiltered() => new();

    public TenantState State => _state;

    public int? HouseholdId => _householdId;

    public int? UserId => _userId;

    public bool IsUnfiltered => _state == TenantState.Unfiltered;

    public bool IsOutOfRequest => _state == TenantState.Unset && _accessor?.HttpContext is null;

    public OutOfRequestMode OutOfRequestMode { get; }

    public bool IsCrossTenantWriteAllowed => _crossTenantWriteDepth > 0;

    public void SetCaller(int householdId, int userId)
    {
        if (_state != TenantState.Unset)
        {
            throw new InvalidOperationException(
                $"SetCaller is valid only from the Unset tenant state; the tenant is {_state}.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(householdId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        _state = TenantState.Caller;
        _householdId = householdId;
        _userId = userId;
    }

    public IDisposable RunAs(int householdId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(householdId);
        if (_state == TenantState.Unfiltered) return NoOpScope.Instance;

        var previous = (_state, _householdId, _userId);
        var scope = new Scope(this, () => (_state, _householdId, _userId) = previous);
        _scopes.Push(scope);

        _state = TenantState.System;
        _householdId = householdId;
        _userId = null;
        return scope;
    }

    public IDisposable AllowCrossTenantWrite(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var scope = new Scope(this, () => _crossTenantWriteDepth--);
        _scopes.Push(scope);
        _crossTenantWriteDepth++;
        return scope;
    }

    private void Close(Scope scope)
    {
        if (!_scopes.TryPeek(out var innermost) || !ReferenceEquals(innermost, scope))
        {
            throw new InvalidOperationException(
                "Tenant scopes must be disposed in LIFO order: this scope is not the innermost open one.");
        }
        _scopes.Pop();
    }

    private sealed class Scope(TenantContext owner, Action restore) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            owner.Close(scope: this); // throws when out of LIFO order, leaving this scope open
            restore();
            _disposed = true;
        }
    }

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();

        public void Dispose() { }
    }
}
