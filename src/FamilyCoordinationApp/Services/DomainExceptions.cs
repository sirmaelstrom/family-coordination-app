namespace FamilyCoordinationApp.Services;

/// <summary>
/// A refusal the caller can act on, thrown by a service and mapped to its HTTP status in ONE place: the /api
/// exception branch (<c>ApiExceptionResponse</c>, quest 940b4022). An endpoint does not catch these.
/// <para><see cref="ClientMessage"/> is what reaches the wire as <c>{ "message": … }</c>; <see cref="Exception.Message"/>
/// may carry log detail (internal ids) and never does. Anything that is not a <see cref="DomainException"/> is a
/// server fault and answers 500.</para>
/// <para><b>Derives from <see cref="Exception"/> on purpose, never from <see cref="InvalidOperationException"/>,
/// <see cref="ArgumentException"/> or <see cref="KeyNotFoundException"/>:</b> a new type inherits every catch its base
/// already has, and the BCL types are what EF and the framework throw for their own faults. Pinned by
/// <c>ExceptionMappingArchitectureTests</c>.</para>
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string clientMessage, string? logDetail = null, Exception? innerException = null)
        : base(logDetail ?? clientMessage, innerException)
    {
        ClientMessage = clientMessage;
    }

    /// <summary>The non-empty, client-safe message written to the response body.</summary>
    public string ClientMessage { get; }

    /// <summary>The HTTP status the /api branch answers with.</summary>
    public abstract int StatusCode { get; }
}

/// <summary>The referenced row does not exist for the caller's household (404).</summary>
public class NotFoundException : DomainException
{
    public NotFoundException(string clientMessage, string? logDetail = null) : base(clientMessage, logDetail)
    {
    }

    public override int StatusCode => StatusCodes.Status404NotFound;
}

/// <summary>
/// The request is well-formed but the domain refuses it: an illegal state transition, an out-of-range value (400).
/// Named <c>Domain…</c> so it never collides with <c>System.ComponentModel.DataAnnotations.ValidationException</c>.
/// </summary>
public class DomainValidationException : DomainException
{
    public DomainValidationException(string clientMessage) : base(clientMessage)
    {
    }

    public override int StatusCode => StatusCodes.Status400BadRequest;
}

/// <summary>The request conflicts with the current state of the resource (409).</summary>
public class ConflictException : DomainException
{
    public ConflictException(string clientMessage, string? logDetail = null, Exception? innerException = null)
        : base(clientMessage, logDetail, innerException)
    {
    }

    public override int StatusCode => StatusCodes.Status409Conflict;
}

/// <summary>
/// An optimistic-concurrency conflict on a row-versioned entity (<see cref="Data.Entities.IVersioned"/>): another
/// writer saved after the caller read it. Raised only by <see cref="Data.ConcurrencySave"/>; the body is a fixed
/// friendly message and never echoes ids.
/// </summary>
public sealed class ConcurrencyConflictException : ConflictException
{
    public const string FixedMessage = "This was changed by someone else. Reload to see the latest.";

    public ConcurrencyConflictException(string logDetail, Exception innerException)
        : base(FixedMessage, logDetail, innerException)
    {
    }
}

/// <summary>The caller is authenticated but may not do this (403). Reserved for the authorization work (quest 82fc7f54).</summary>
public class ForbiddenException : DomainException
{
    public ForbiddenException(string clientMessage, string? logDetail = null) : base(clientMessage, logDetail)
    {
    }

    public override int StatusCode => StatusCodes.Status403Forbidden;
}
