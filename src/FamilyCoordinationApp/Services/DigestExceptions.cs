namespace FamilyCoordinationApp.Services;

/// <summary>
/// Thrown when a digest settings mutation fails domain validation (invalid <c>SendHourLocal</c>,
/// <c>SendDayOfWeek</c>, or <c>Cadence</c>). The endpoint layer (WP-06) maps this to
/// <c>400 Bad Request</c>.
/// </summary>
public class DigestSettingsValidationException : Exception
{
    public DigestSettingsValidationException(string message) : base(message)
    {
    }
}
