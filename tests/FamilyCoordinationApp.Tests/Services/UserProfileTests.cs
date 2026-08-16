using FamilyCoordinationApp.Services;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// <c>Initials</c> is now computed where a user is created, not backfilled by the authorization handler, so this
/// derivation is on the path of every avatar the app renders.
/// </summary>
public sealed class UserProfileTests
{
    [Theory]
    [InlineData("Alice Anderson", "AA")]              // first + last
    [InlineData("Alice Beth Anderson", "AA")]         // middle names are skipped, not included
    [InlineData("newbie", "N")]                       // single token (email local-part)
    [InlineData("  ", "?")]                           // no usable name
    [InlineData(null, "?")]
    public void ComputeInitials(string? displayName, string expected) =>
        UserProfile.ComputeInitials(displayName).Should().Be(expected);
}
