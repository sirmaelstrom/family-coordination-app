using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>The real <see cref="IKeyAllocator"/> for tests that construct a service directly (it holds no state).</summary>
public static class TestKeys
{
    public static IKeyAllocator Allocator { get; } = new KeyAllocator(NullLogger<KeyAllocator>.Instance);
}
