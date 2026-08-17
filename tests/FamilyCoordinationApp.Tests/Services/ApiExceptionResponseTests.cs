using FluentAssertions;
using Microsoft.AspNetCore.Http;
using FamilyCoordinationApp.Endpoints;

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
        ApiExceptionResponse.StatusFor(null).Should().Be(500);
    }
}
