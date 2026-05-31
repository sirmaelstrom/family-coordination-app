using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Endpoints;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// WP-06 backend tests for the new <see cref="IImageService.SaveImageAsync(IFormFile, int, CancellationToken)"/>
/// overload (the Minimal-API multipart upload path, A1). Asserts it enforces the SAME size / extension /
/// content-type validation as the <see cref="Microsoft.AspNetCore.Components.Forms.IBrowserFile"/> path so the
/// new door is not weaker (M8). Class name contains "ImageService" so the WP verification filter
/// (<c>--filter "FullyQualifiedName~ImageService"</c>) selects it.
/// </summary>
public class ImageServiceFormFileTests : IDisposable
{
    private readonly string _webRoot;
    private readonly ImageService _imageService;

    public ImageServiceFormFileTests()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), "ImageServiceFormFileTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_webRoot);
        var envMock = new Mock<IWebHostEnvironment>();
        envMock.Setup(e => e.WebRootPath).Returns(_webRoot);
        _imageService = new ImageService(envMock.Object, new Mock<ILogger<ImageService>>().Object);
    }

    [Fact]
    public async Task SaveImageAsync_IFormFile_SavesValidImage_ReturnsHouseholdScopedPath()
    {
        var file = MakeFormFile("photo.png", "image/png", new byte[] { 1, 2, 3, 4 });

        var path = await _imageService.SaveImageAsync(file, householdId: 7);

        path.Should().StartWith("/uploads/7/");
        path.Should().EndWith(".png");
        File.Exists(Path.Combine(_webRoot, "uploads", "7", Path.GetFileName(path))).Should().BeTrue();
    }

    [Fact]
    public async Task SaveImageAsync_IFormFile_RejectsDisallowedExtension()
    {
        var file = MakeFormFile("malware.exe", "image/png", new byte[] { 1, 2, 3 });

        var act = () => _imageService.SaveImageAsync(file, householdId: 1);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not allowed*");
    }

    [Fact]
    public async Task SaveImageAsync_IFormFile_RejectsDisallowedContentType()
    {
        var file = MakeFormFile("photo.png", "application/octet-stream", new byte[] { 1, 2, 3 });

        var act = () => _imageService.SaveImageAsync(file, householdId: 1);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Content type*not allowed*");
    }

    [Fact]
    public async Task SaveImageAsync_IFormFile_RejectsOversizedFile()
    {
        // 11 MB > 10 MB MaxFileSize. Use a Length that exceeds the cap without allocating the bytes.
        var file = new FormFile(new MemoryStream(new byte[] { 0 }), 0, 11L * 1024 * 1024, "file", "big.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };

        var act = () => _imageService.SaveImageAsync(file, householdId: 1);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exceeds maximum*");
    }

    private static FormFile MakeFormFile(string fileName, string contentType, byte[] content)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_webRoot))
        {
            try { Directory.Delete(_webRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}

/// <summary>
/// WP-06 backend test for the <c>PATCH /api/chores/me/default-view</c> setter core
/// (<see cref="ChoresEndpoints.ApplyDefaultViewAsync"/>): persists a valid lens per-user, clears to default on
/// null, rejects an unknown lens id without mutating, and is scoped to the caller only (council M10/M6/M1).
/// </summary>
public class ChoreDefaultViewSetterTests
{
    [Fact]
    public async Task ApplyDefaultView_PersistsValidLens_ForCaller()
    {
        var (factory, _) = NewFactory(out var options);
        SeedUser(options, userId: 1, householdId: 1, view: null);

        var (outcome, normalized) =
            await ChoresEndpoints.ApplyDefaultViewAsync(factory, userId: 1, ChoreLens.Mine, CancellationToken.None);

        outcome.Should().Be(ChoresEndpoints.DefaultViewOutcome.Ok);
        normalized.Should().Be(ChoreLens.Mine);
        ReadView(options, userId: 1).Should().Be(ChoreLens.Mine);
    }

    [Fact]
    public async Task ApplyDefaultView_NullClearsToDefault()
    {
        var (factory, _) = NewFactory(out var options);
        SeedUser(options, userId: 1, householdId: 1, view: ChoreLens.Rooms);

        var (outcome, normalized) =
            await ChoresEndpoints.ApplyDefaultViewAsync(factory, userId: 1, null, CancellationToken.None);

        outcome.Should().Be(ChoresEndpoints.DefaultViewOutcome.Ok);
        normalized.Should().BeNull();
        ReadView(options, userId: 1).Should().BeNull();
    }

    [Fact]
    public async Task ApplyDefaultView_RejectsUnknownLensId_WithoutMutating()
    {
        var (factory, _) = NewFactory(out var options);
        SeedUser(options, userId: 1, householdId: 1, view: ChoreLens.Mine);

        var (outcome, normalized) =
            await ChoresEndpoints.ApplyDefaultViewAsync(factory, userId: 1, "all-the-chores", CancellationToken.None);

        outcome.Should().Be(ChoresEndpoints.DefaultViewOutcome.InvalidLens);
        normalized.Should().BeNull();
        ReadView(options, userId: 1).Should().Be(ChoreLens.Mine); // unchanged
    }

    [Fact]
    public async Task ApplyDefaultView_IsScopedToCaller_DoesNotTouchOtherUser()
    {
        var (factory, _) = NewFactory(out var options);
        SeedUser(options, userId: 1, householdId: 1, view: null);
        SeedUser(options, userId: 2, householdId: 1, view: ChoreLens.Rooms);

        await ChoresEndpoints.ApplyDefaultViewAsync(factory, userId: 1, ChoreLens.UpForGrabs, CancellationToken.None);

        ReadView(options, userId: 1).Should().Be(ChoreLens.UpForGrabs);
        ReadView(options, userId: 2).Should().Be(ChoreLens.Rooms); // other caller untouched
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private static (IDbContextFactory<ApplicationDbContext> Factory, DbContextOptions<ApplicationDbContext> Options)
        NewFactory(out DbContextOptions<ApplicationDbContext> options)
    {
        options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var captured = options;
        var mock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(captured));
        return (mock.Object, options);
    }

    private static void SeedUser(DbContextOptions<ApplicationDbContext> options, int userId, int householdId, string? view)
    {
        using var ctx = new ApplicationDbContext(options);
        if (!ctx.Households.Any(h => h.Id == householdId))
        {
            ctx.Households.Add(new Household { Id = householdId, Name = $"H{householdId}" });
        }
        ctx.Users.Add(new User
        {
            Id = userId,
            HouseholdId = householdId,
            Email = $"u{userId}@b.com",
            DisplayName = $"User{userId}",
            ChoresDefaultView = view
        });
        ctx.SaveChanges();
    }

    private static string? ReadView(DbContextOptions<ApplicationDbContext> options, int userId)
    {
        using var ctx = new ApplicationDbContext(options);
        return ctx.Users.Single(u => u.Id == userId).ChoresDefaultView;
    }
}
