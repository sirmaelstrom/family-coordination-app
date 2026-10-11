namespace FamilyCoordinationApp.Services;

/// <summary>
/// Thrown when a digest settings mutation fails domain validation (invalid <c>SendHourLocal</c>,
/// <c>SendDayOfWeek</c>, or <c>Cadence</c>). The /api exception branch maps it to <c>400 Bad Request</c>;
/// the message is client-facing.
/// </summary>
public class DigestSettingsValidationException : DomainValidationException
{
    public DigestSettingsValidationException(string message) : base(message)
    {
    }
}
