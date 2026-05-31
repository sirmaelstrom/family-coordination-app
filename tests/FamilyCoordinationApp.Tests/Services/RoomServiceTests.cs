using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

public class RoomServiceTests : IDisposable
{
    private readonly ApplicationDbContext _context;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly RoomService _service;
    private readonly ImageService _imageService;
    private readonly string _webRoot;

    public RoomServiceTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(_options);

        var dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        // Real ImageService backed by a temp WebRootPath so disk delete-on-replace /
        // delete-before-delete is verifiable against the actual filesystem.
        _webRoot = Path.Combine(Path.GetTempPath(), "RoomServiceTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_webRoot);
        var envMock = new Mock<IWebHostEnvironment>();
        envMock.Setup(e => e.WebRootPath).Returns(_webRoot);
        _imageService = new ImageService(envMock.Object, new Mock<ILogger<ImageService>>().Object);

        _service = new RoomService(
            dbFactoryMock.Object,
            _imageService,
            new Mock<ILogger<RoomService>>().Object);

        SeedTestData();
    }

    private void SeedTestData()
    {
        _context.Households.AddRange(
            new Household { Id = 1, Name = "Smith Family" },
            new Household { Id = 2, Name = "Jones Family" }
        );

        _context.Users.Add(new User { Id = 1, HouseholdId = 1, Email = "a@b.com", DisplayName = "Alice" });

        // Household 1 rooms
        _context.Rooms.AddRange(
            new Room { HouseholdId = 1, RoomId = 1, Name = "Kitchen", Icon = "🍳", SortOrder = 1, CreatedAt = DateTime.UtcNow },
            new Room { HouseholdId = 1, RoomId = 2, Name = "Bathroom", Icon = "🛁", SortOrder = 2, CreatedAt = DateTime.UtcNow }
        );

        // Household 2 room (same RoomId=1 — isolation check)
        _context.Rooms.Add(
            new Room { HouseholdId = 2, RoomId = 1, Name = "Garage", Icon = "🚗", SortOrder = 1, CreatedAt = DateTime.UtcNow }
        );

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        if (Directory.Exists(_webRoot))
        {
            try { Directory.Delete(_webRoot, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>Writes a real file under WebRoot for the given /uploads-style relative URL and returns the URL.</summary>
    private string CreatePhotoFile(int householdId, string fileName)
    {
        var dir = Path.Combine(_webRoot, "uploads", householdId.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "fake-image-bytes");
        return $"/uploads/{householdId}/{fileName}";
    }

    private string FullPath(string urlPath) =>
        Path.Combine(_webRoot, urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    // ---- CRUD + isolation -------------------------------------------------

    [Fact]
    public async Task ListRoomsAsync_ReturnsOnlyCurrentHouseholdsRooms_OrderedBySortOrder()
    {
        var h1 = await _service.ListRoomsAsync(householdId: 1);
        var h2 = await _service.ListRoomsAsync(householdId: 2);

        h1.Should().HaveCount(2);
        h1.Should().OnlyContain(r => r.HouseholdId == 1);
        h1.Select(r => r.Name).Should().ContainInOrder("Kitchen", "Bathroom");

        h2.Should().HaveCount(1);
        h2.Should().OnlyContain(r => r.HouseholdId == 2);
        h2.Single().Name.Should().Be("Garage");
    }

    [Fact]
    public async Task CreateRoomAsync_AssignsNextRoomIdAndSortOrderForHousehold()
    {
        var created = await _service.CreateRoomAsync(householdId: 1, name: "Living Room", icon: "🛋️");

        created.RoomId.Should().Be(3);       // h1 already has rooms 1,2
        created.SortOrder.Should().Be(3);     // appended after max sort order 2
        created.HouseholdId.Should().Be(1);
        created.Name.Should().Be("Living Room");

        // Round-trips and isolated to household 1
        var h1 = await _service.ListRoomsAsync(householdId: 1);
        h1.Should().HaveCount(3);
        var h2 = await _service.ListRoomsAsync(householdId: 2);
        h2.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateRoomAsync_TrimsNameAndIcon()
    {
        var created = await _service.CreateRoomAsync(householdId: 1, name: "  Office  ", icon: "  📚 ");

        created.Name.Should().Be("Office");
        created.Icon.Should().Be("📚");
    }

    [Fact]
    public async Task GetRoomAsync_IsHouseholdScoped()
    {
        // Both households have RoomId 1, but with different names.
        var h1Room1 = await _service.GetRoomAsync(householdId: 1, roomId: 1);
        var h2Room1 = await _service.GetRoomAsync(householdId: 2, roomId: 1);

        h1Room1!.Name.Should().Be("Kitchen");
        h2Room1!.Name.Should().Be("Garage");

        // A room id that only exists in another household is not visible.
        var missing = await _service.GetRoomAsync(householdId: 2, roomId: 2);
        missing.Should().BeNull();
    }

    [Fact]
    public async Task UpdateRoomAsync_UpdatesTheRightRoom_AndDoesNotLeakAcrossHouseholds()
    {
        var result = await _service.UpdateRoomAsync(householdId: 1, roomId: 1, name: "Big Kitchen", icon: "🔪", photoPath: null);

        result.Name.Should().Be("Big Kitchen");
        result.Icon.Should().Be("🔪");

        // Household 2's RoomId 1 is unchanged.
        var h2Room1 = await _service.GetRoomAsync(householdId: 2, roomId: 1);
        h2Room1!.Name.Should().Be("Garage");
    }

    [Fact]
    public async Task UpdateRoomAsync_ThrowsWhenRoomNotFound()
    {
        var act = async () => await _service.UpdateRoomAsync(householdId: 1, roomId: 999, name: "X", icon: "?", photoPath: null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*999*not found*");
    }

    // ---- Image lifecycle: delete-on-replace (M8) --------------------------

    [Fact]
    public async Task UpdateRoomAsync_ReplacingPhoto_DeletesOldFileFromDisk()
    {
        // Arrange: room with an existing photo file on disk.
        var oldUrl = CreatePhotoFile(householdId: 1, fileName: "old.png");
        var newUrl = CreatePhotoFile(householdId: 1, fileName: "new.png");
        var seeded = await _service.GetRoomAsync(1, 2);
        await _service.UpdateRoomAsync(1, 2, seeded!.Name, seeded.Icon, oldUrl);
        File.Exists(FullPath(oldUrl)).Should().BeTrue("old photo was just attached");

        // Act: replace with a different photo.
        await _service.UpdateRoomAsync(1, 2, seeded.Name, seeded.Icon, newUrl);

        // Assert: old file deleted from disk, new file still present, DB points at new.
        File.Exists(FullPath(oldUrl)).Should().BeFalse("the replaced photo must be removed from disk (M8)");
        File.Exists(FullPath(newUrl)).Should().BeTrue();
        (await _service.GetRoomAsync(1, 2))!.PhotoPath.Should().Be(newUrl);
    }

    [Fact]
    public async Task UpdateRoomAsync_SamePhotoPath_DoesNotDeleteFile()
    {
        var url = CreatePhotoFile(householdId: 1, fileName: "keep.png");
        var seeded = await _service.GetRoomAsync(1, 2);
        await _service.UpdateRoomAsync(1, 2, seeded!.Name, seeded.Icon, url);

        // Act: update with the SAME photo path (e.g. only the name changed).
        await _service.UpdateRoomAsync(1, 2, "Renamed Bath", seeded.Icon, url);

        // Assert: file untouched.
        File.Exists(FullPath(url)).Should().BeTrue("unchanged photo path must not be deleted");
        (await _service.GetRoomAsync(1, 2))!.PhotoPath.Should().Be(url);
    }

    [Fact]
    public async Task UpdateRoomAsync_FromNullToPhoto_DoesNotThrow_NoFileToDelete()
    {
        var url = CreatePhotoFile(householdId: 1, fileName: "first.png");

        var act = async () => await _service.UpdateRoomAsync(1, 1, "Kitchen", "🍳", url);

        await act.Should().NotThrowAsync();
        File.Exists(FullPath(url)).Should().BeTrue();
    }

    // ---- Image lifecycle + chore null-out: delete (M8 + council M3) -------

    [Fact]
    public async Task DeleteRoomAsync_DeletesPhotoFileFromDisk()
    {
        var url = CreatePhotoFile(householdId: 1, fileName: "todelete.png");
        var seeded = await _service.GetRoomAsync(1, 1);
        await _service.UpdateRoomAsync(1, 1, seeded!.Name, seeded.Icon, url);
        File.Exists(FullPath(url)).Should().BeTrue();

        await _service.DeleteRoomAsync(householdId: 1, roomId: 1);

        File.Exists(FullPath(url)).Should().BeFalse("deleting a room must delete its photo file (M8)");
        (await _service.GetRoomAsync(1, 1)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteRoomAsync_WithNullPhoto_IsNoOpForDisk_AndRemovesRoom()
    {
        // Room 2 has no photo.
        var act = async () => await _service.DeleteRoomAsync(householdId: 1, roomId: 2);

        await act.Should().NotThrowAsync();
        (await _service.GetRoomAsync(1, 2)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteRoomAsync_NullsOutRoomIdOnAffectedChores_LeavingChoresIntact()
    {
        // Arrange: two chores in room 1, one in room 2, one already unassigned (General).
        await using (var ctx = new ApplicationDbContext(_options))
        {
            ctx.Chores.AddRange(
                MakeChore(ctx, choreId: 1, roomId: 1, name: "Wash dishes"),
                MakeChore(ctx, choreId: 2, roomId: 1, name: "Mop floor"),
                MakeChore(ctx, choreId: 3, roomId: 2, name: "Clean tub"),
                MakeChore(ctx, choreId: 4, roomId: null, name: "Take out trash")
            );
            await ctx.SaveChangesAsync();
        }

        // Act: delete room 1.
        await _service.DeleteRoomAsync(householdId: 1, roomId: 1);

        // Assert: room gone; its chores survive with RoomId == null; other rooms' chores untouched.
        await using var verify = new ApplicationDbContext(_options);
        verify.Rooms.Any(r => r.HouseholdId == 1 && r.RoomId == 1).Should().BeFalse();

        var allChores = await verify.Chores.Where(c => c.HouseholdId == 1).ToListAsync();
        allChores.Should().HaveCount(4, "no chores may be deleted when a room is deleted");

        allChores.Single(c => c.ChoreId == 1).RoomId.Should().BeNull("room-1 chore must fall back to General");
        allChores.Single(c => c.ChoreId == 2).RoomId.Should().BeNull("room-1 chore must fall back to General");
        allChores.Single(c => c.ChoreId == 3).RoomId.Should().Be(2, "room-2 chore is unaffected");
        allChores.Single(c => c.ChoreId == 4).RoomId.Should().BeNull("already-General chore is unchanged");
    }

    [Fact]
    public async Task DeleteRoomAsync_DoesNotNullChoresInOtherHouseholds()
    {
        // Household 2 also has a RoomId 1 with a chore in it.
        await using (var ctx = new ApplicationDbContext(_options))
        {
            var c = MakeChore(ctx, choreId: 1, roomId: 1, name: "Sweep garage");
            c.HouseholdId = 2;
            c.EnteredByUserId = 1; // user 1 belongs to h1 but FK is Restrict-only; fine for InMemory
            ctx.Chores.Add(c);
            await ctx.SaveChangesAsync();
        }

        // Act: delete household 1's room 1.
        await _service.DeleteRoomAsync(householdId: 1, roomId: 1);

        // Assert: household 2's chore still references room 1.
        await using var verify = new ApplicationDbContext(_options);
        var h2Chore = await verify.Chores.SingleAsync(c => c.HouseholdId == 2 && c.ChoreId == 1);
        h2Chore.RoomId.Should().Be(1, "a delete in household 1 must not touch household 2's chores (M1)");
    }

    [Fact]
    public async Task DeleteRoomAsync_ThrowsWhenRoomNotFound()
    {
        var act = async () => await _service.DeleteRoomAsync(householdId: 1, roomId: 999);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*999*not found*");
    }

    // ---- Reorder ----------------------------------------------------------

    [Fact]
    public async Task ReorderAsync_SetsSortOrderToIndex_ScopedToHousehold()
    {
        // Reverse the order: room 2 first, room 1 second.
        await _service.ReorderAsync(householdId: 1, orderedRoomIds: new[] { 2, 1 });

        var rooms = await _service.ListRoomsAsync(householdId: 1);
        rooms.Select(r => r.RoomId).Should().ContainInOrder(2, 1);
        rooms.Single(r => r.RoomId == 2).SortOrder.Should().Be(0);
        rooms.Single(r => r.RoomId == 1).SortOrder.Should().Be(1);
    }

    [Fact]
    public async Task ReorderAsync_IgnoresRoomIdsFromOtherHouseholds()
    {
        // Includes room id 1 (exists in both households) — only household 1's row should change.
        await _service.ReorderAsync(householdId: 1, orderedRoomIds: new[] { 1, 2 });

        var h2Room1 = await _service.GetRoomAsync(householdId: 2, roomId: 1);
        h2Room1!.SortOrder.Should().Be(1, "household 2's room must be untouched by household 1's reorder");
    }

    // -----------------------------------------------------------------------

    private static Chore MakeChore(ApplicationDbContext ctx, int choreId, int? roomId, string name)
    {
        return new Chore
        {
            HouseholdId = 1,
            ChoreId = choreId,
            Name = name,
            RoomId = roomId,
            RecurrenceMode = RecurrenceMode.OneOff,
            EffortTier = EffortTier.Standard,
            EffortPoints = 1,
            Status = ChoreStatus.Active,
            AssignmentKind = AssignmentKind.None,
            EnteredByUserId = 1,
            CreatedAt = DateTime.UtcNow
        };
    }
}
