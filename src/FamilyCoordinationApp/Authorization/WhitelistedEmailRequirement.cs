using Microsoft.AspNetCore.Authorization;

namespace FamilyCoordinationApp.Authorization;

public class WhitelistedEmailRequirement : IAuthorizationRequirement
{
    // Marker class - no properties needed
}
