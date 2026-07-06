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
    public async Task DeleteRoomAsync_RemovesMembership_MultiRoomChoreKeepsOtherRooms_LeavingChoresIntact()
    {
        // Arrange (Phase 13 M:N): a chore only in room 1; a chore in rooms 1 AND 2; a chore only in room 2;
        // a General chore (no memberships).
        await using (var ctx = new ApplicationDbContext(_options))
        {
            SeedChoreWithRooms(ctx, householdId: 1, choreId: 1, name: "Wash dishes", roomIds: new[] { 1 });
            SeedChoreWithRooms(ctx, householdId: 1, choreId: 2, name: "Mop floor", roomIds: new[] { 1, 2 });
            SeedChoreWithRooms(ctx, householdId: 1, choreId: 3, name: "Clean tub", roomIds: new[] { 2 });
            SeedChoreWithRooms(ctx, householdId: 1, choreId: 4, name: "Take out trash", roomIds: Array.Empty<int>());
            await ctx.SaveChangesAsync();
        }

        // Act: delete room 1.
        await _service.DeleteRoomAsync(householdId: 1, roomId: 1);

        // Assert: room gone; NO chores deleted; memberships + shim updated correctly.
        await using var verify = new ApplicationDbContext(_options);
        verify.Rooms.Any(r => r.HouseholdId == 1 && r.RoomId == 1).Should().BeFalse();

        var allChores = await verify.Chores.Where(c => c.HouseholdId == 1).ToListAsync();
        allChores.Should().HaveCount(4, "a room delete removes memberships, never chores");

        // Chore 1 (only room 1) → General.
        (await MembershipsAsync(1, 1)).Should().BeEmpty("a chore only in the deleted room falls to General");
        allChores.Single(c => c.ChoreId == 1).RoomId.Should().BeNull();

        // Chore 2 (rooms 1+2) → keeps room 2; shim recomputes to the min remaining (2).
        (await MembershipsAsync(1, 2)).Should().Equal(new[] { 2 }, "a multi-room chore keeps its other rooms");
        allChores.Single(c => c.ChoreId == 2).RoomId.Should().Be(2);

        // Chore 3 (only room 2) → untouched.
        (await MembershipsAsync(1, 3)).Should().Equal(new[] { 2 });
        allChores.Single(c => c.ChoreId == 3).RoomId.Should().Be(2, "a chore in another room is unaffected");

        // Chore 4 (General) → unchanged.
        (await MembershipsAsync(1, 4)).Should().BeEmpty();
        allChores.Single(c => c.ChoreId == 4).RoomId.Should().BeNull("an already-General chore is unchanged");
    }

    [Fact]
    public async Task DeleteRoomAsync_DoesNotTouchOtherHouseholdsMemberships()
    {
        // Household 2 also has a RoomId 1 with a chore membership in it (same RoomId as household 1 — the
        // isolation trap).
        await using (var ctx = new ApplicationDbContext(_options))
        {
            SeedChoreWithRooms(ctx, householdId: 2, choreId: 1, name: "Sweep garage", roomIds: new[] { 1 });
            await ctx.SaveChangesAsync();
        }

        // Act: delete household 1's room 1.
        await _service.DeleteRoomAsync(householdId: 1, roomId: 1);

        // Assert: household 2's chore keeps its room-1 membership + shim (M1).
        (await MembershipsAsync(2, 1)).Should().Equal(new[] { 1 }, "a delete in household 1 must not touch household 2 (M1)");
        await using var verify = new ApplicationDbContext(_options);
        var h2Chore = await verify.Chores.SingleAsync(c => c.HouseholdId == 2 && c.ChoreId == 1);
        h2Chore.RoomId.Should().Be(1);
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

    /// <summary>
    /// Seed a chore with 0..N room memberships (Phase 13): writes the Chore + one ChoreRoom row per id, and
    /// the dual-write shim RoomId = the min membership (or null). Mirrors what ChoreService.CreateChoreAsync
    /// persists, so the room-delete path sees realistic membership data.
    /// </summary>
    private static void SeedChoreWithRooms(ApplicationDbContext ctx, int householdId, int choreId, string name, int[] roomIds)
    {
        var distinct = roomIds.Distinct().OrderBy(x => x).ToArray();
        ctx.Chores.Add(new Chore
        {
            HouseholdId = householdId,
            ChoreId = choreId,
            Name = name,
            RoomId = distinct.Length == 0 ? null : distinct[0],
            RecurrenceMode = RecurrenceMode.OneOff,
            EffortTier = EffortTier.Standard,
            EffortPoints = 1,
            Status = ChoreStatus.Active,
            AssignmentKind = AssignmentKind.None,
            EnteredByUserId = 1,
            CreatedAt = DateTime.UtcNow
        });

        foreach (var roomId in distinct)
        {
            ctx.ChoreRooms.Add(new ChoreRoom { HouseholdId = householdId, ChoreId = choreId, RoomId = roomId });
        }
    }

    private async Task<List<int>> MembershipsAsync(int householdId, int choreId)
    {
        await using var ctx = new ApplicationDbContext(_options);
        return await ctx.ChoreRooms
            .Where(cr => cr.HouseholdId == householdId && cr.ChoreId == choreId)
            .OrderBy(cr => cr.RoomId)
            .Select(cr => cr.RoomId)
            .ToListAsync();
    }
}
