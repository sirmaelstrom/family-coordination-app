using FluentAssertions;
using Microsoft.AspNetCore.Http;
using FamilyCoordinationApp.Endpoints;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Status mapping for the /api exception branch. The unhandled-500 half is asserted here because no
/// production route throws on demand — the integration tests cover the reachable
/// <see cref="BadHttpRequestException"/> class end-to-end.
/// </summary>
public class ApiExceptionResponseTests
{
    [Fact]
    public void BadHttpRequestException_KeepsItsOwnStatus()
    {
        ApiExceptionResponse.StatusFor(new BadHttpRequestException("missing body", StatusCodes.Status400BadRequest))
            .Should().Be(400);
        ApiExceptionResponse.StatusFor(new BadHttpRequestException("too large", StatusCodes.Status413PayloadTooLarge))
            .Should().Be(413);
    }

    [Fact]
    public void AnyOtherException_IsAServerFault()
    {
        ApiExceptionResponse.StatusFor(new InvalidOperationException("boom")).Should().Be(500);
        // The BCL types services used to throw for not-found / invalid are server faults now (quest 940b4022).
        ApiExceptionResponse.StatusFor(new KeyNotFoundException("boom")).Should().Be(500);
        ApiExceptionResponse.StatusFor(new ArgumentException("boom")).Should().Be(500);
        ApiExceptionResponse.StatusFor(null).Should().Be(500);
        ApiExceptionResponse.MessageFor(new InvalidOperationException("Chore 7 secret detail"), 500)
            .Should().Be("Something went wrong on our end.", "nothing from a server fault reaches the wire");
    }

    [Fact]
    public void A_DomainException_answers_its_own_status_with_its_client_message_never_its_log_detail()
    {
        var cases = new (DomainException Error, int Status, string Message)[]
        {
            (new NotFoundException("Room not found.", "Room 7 not found for household 3"), 404, "Room not found."),
            (new ChoreNotFoundException("Chore 7 not found for household 3."), 404, "Chore not found."),
            (new DomainValidationException("Servings must be 1000 or fewer."), 400, "Servings must be 1000 or fewer."),
            (new ChoreValidationException("Name is required."), 400, "Name is required."),
            (new ConflictException("The linked meal plan no longer exists.", "MealPlan 9 not found"), 409, "The linked meal plan no longer exists."),
            (new ConcurrencyConflictException("Concurrency conflict saving Chore", new Exception()), 409, ConcurrencyConflictException.FixedMessage),
            (new ForbiddenException("No."), 403, "No."),
        };

        foreach (var (error, status, message) in cases)
        {
            ApiExceptionResponse.StatusFor(error).Should().Be(status, error.GetType().Name);
            ApiExceptionResponse.MessageFor(error, status).Should().Be(message, error.GetType().Name);
            ApiExceptionResponse.MessageFor(error, status).Should().NotContain("7", "log detail with ids stays off the wire");
        }
    }
}
