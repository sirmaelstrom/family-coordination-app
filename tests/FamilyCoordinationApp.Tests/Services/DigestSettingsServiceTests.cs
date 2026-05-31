using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Unit tests for <see cref="DigestSettingsService"/>.
/// Uses an InMemory EF provider and an ephemeral DataProtection provider (no cert needed).
/// Key security assertions:
///  - <c>WebhookUrlProtected</c> in the raw entity is NEVER equal to the plaintext URL (always ciphertext, M8/MN7).
///  - <c>GetAsync</c> never returns a URL — only <c>HasWebhook</c> + a masked hint.
///  - <c>GetDecryptedWebhookAsync</c> round-trips the original URL without leaking to the view.
///  - HouseholdId isolation (M1).
/// </summary>
public class DigestSettingsServiceTests : IDisposable
{
    private const int H1 = 1;
    private const int H2 = 2;

    private const string TestWebhookUrl = "https://discord.com/api/webhooks/111222333/abcdefghijklmnop-SECRET";

    private static readonly DateTime FixedUtc = new(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _seedCtx;
    private readonly DigestSettingsService _service;
    private readonly IDataProtectionProvider _dpProvider;

    public DigestSettingsServiceTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _seedCtx = new ApplicationDbContext(_options);
        SeedHouseholds();

        // Ephemeral DataProtection provider — no key persistence, no cert required.
        // Keys are generated fresh per test run (in-memory only).
        var services = new ServiceCollection();
        services.AddDataProtection()
                .UseEphemeralDataProtectionProvider();
        var sp = services.BuildServiceProvider();
        _dpProvider = sp.GetRequiredService<IDataProtectionProvider>();

        var dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        _service = new DigestSettingsService(
            dbFactoryMock.Object,
            _dpProvider,
            new FixedTimeProvider(FixedUtc),
            new Mock<ILogger<DigestSettingsService>>().Object);
    }

    private void SeedHouseholds()
    {
        _seedCtx.Households.AddRange(
            new Household { Id = H1, Name = "Alpha Family" },
            new Household { Id = H2, Name = "Beta Family" });
        _seedCtx.SaveChanges();
    }

    public void Dispose()
    {
        _seedCtx.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── GetAsync returns defaults when no row ────────────────────────────────

    [Fact]
    public async Task GetAsync_NoRow_ReturnsDefaults()
    {
        var view = await _service.GetAsync(H1);

        view.Enabled.Should().BeFalse();
        view.Cadence.Should().Be(DigestCadence.Weekly);
        view.SendDayOfWeek.Should().Be(DayOfWeek.Sunday);
        view.SendHourLocal.Should().Be(18);
        view.HasWebhook.Should().BeFalse();
        view.WebhookHint.Should().BeNull();
        view.LastSentAt.Should().BeNull();
    }

    // ── UpdateAsync encrypts the webhook — never stores plaintext ────────────

    [Fact]
    public async Task UpdateAsync_StoresEncryptedWebhook_NeverPlaintext()
    {
        var update = MakeUpdate(webhookUrl: TestWebhookUrl);
        await _service.UpdateAsync(H1, update);

        // Read the raw entity directly — bypass the service.
        await using var ctx = new ApplicationDbContext(_options);
        var row = await ctx.ChoreDigestSettings.SingleAsync(s => s.HouseholdId == H1);

        row.WebhookUrlProtected.Should().NotBeNullOrEmpty("ciphertext must be stored");
        row.WebhookUrlProtected.Should().NotBe(TestWebhookUrl,
            "the stored value must be ciphertext, never the plaintext URL (M8/MN7)");
    }

    // ── GetDecryptedWebhookAsync round-trips to the original URL ─────────────

    [Fact]
    public async Task GetDecryptedWebhookAsync_RoundTripsToOriginalUrl()
    {
        await _service.UpdateAsync(H1, MakeUpdate(webhookUrl: TestWebhookUrl));

        var decrypted = await _service.GetDecryptedWebhookAsync(H1);

        decrypted.Should().Be(TestWebhookUrl,
            "Unprotect must return the exact original URL");
    }

    // ── GetAsync returns hasWebhook=true + hint, never the URL ───────────────

    [Fact]
    public async Task GetAsync_AfterWebhookSet_ReturnsHasWebhookTrue_AndMaskedHint_NeverUrl()
    {
        await _service.UpdateAsync(H1, MakeUpdate(webhookUrl: TestWebhookUrl));

        var view = await _service.GetAsync(H1);

        view.HasWebhook.Should().BeTrue();
        view.WebhookHint.Should().NotBeNullOrEmpty("a hint must be present once a webhook is stored");

        // MN7: the full URL and the ciphertext must never appear in the view.
        view.WebhookHint.Should().NotBe(TestWebhookUrl, "full URL must never be in the hint");
        view.WebhookHint!.Length.Should().BeLessThanOrEqualTo(4,
            "hint is at most 4 chars (last 4 of the URL)");

        // Confirm the hint matches the expected last 4 chars.
        var expectedHint = TestWebhookUrl[^4..];
        view.WebhookHint.Should().Be(expectedHint);
    }

    // ── Tri-state: omitted (WebhookProvided=false) leaves webhook unchanged ──

    [Fact]
    public async Task UpdateAsync_WebhookNotProvided_LeavesStoredWebhookUnchanged()
    {
        // First set a webhook.
        await _service.UpdateAsync(H1, MakeUpdate(webhookUrl: TestWebhookUrl));

        // Then update with webhook omitted (WebhookProvided=false — the default).
        var omittedUpdate = new DigestSettingsUpdate(
            Enabled: false,
            Cadence: DigestCadence.Weekly,
            SendDayOfWeek: DayOfWeek.Monday,
            SendHourLocal: 9);
        await _service.UpdateAsync(H1, omittedUpdate);

        // Webhook should still be present and decryptable.
        var decrypted = await _service.GetDecryptedWebhookAsync(H1);
        decrypted.Should().Be(TestWebhookUrl, "omitting the webhook field must not clear it");

        var view = await _service.GetAsync(H1);
        view.HasWebhook.Should().BeTrue();
        view.SendDayOfWeek.Should().Be(DayOfWeek.Monday, "other fields are still updated");
    }

    // ── Tri-state: explicit null clears the webhook ───────────────────────────

    [Fact]
    public async Task UpdateAsync_WebhookProvidedAsNull_ClearsStoredWebhook()
    {
        await _service.UpdateAsync(H1, MakeUpdate(webhookUrl: TestWebhookUrl));

        // Explicit null with WebhookProvided=true — should clear.
        var clearUpdate = new DigestSettingsUpdate(
            Enabled: true,
            Cadence: DigestCadence.Weekly,
            SendDayOfWeek: DayOfWeek.Friday,
            SendHourLocal: 8,
            WebhookProvided: true,
            WebhookUrl: null);
        await _service.UpdateAsync(H1, clearUpdate);

        var view = await _service.GetAsync(H1);
        view.HasWebhook.Should().BeFalse("explicit null must clear the stored webhook");
        view.WebhookHint.Should().BeNull();

        var decrypted = await _service.GetDecryptedWebhookAsync(H1);
        decrypted.Should().BeNull("no webhook → GetDecryptedWebhookAsync returns null");

        // Confirm raw entity is null.
        await using var ctx = new ApplicationDbContext(_options);
        var row = await ctx.ChoreDigestSettings.SingleAsync(s => s.HouseholdId == H1);
        row.WebhookUrlProtected.Should().BeNull();
    }

    // ── Tri-state: explicit empty string clears the webhook ───────────────────

    [Fact]
    public async Task UpdateAsync_WebhookProvidedAsEmptyString_ClearsStoredWebhook()
    {
        await _service.UpdateAsync(H1, MakeUpdate(webhookUrl: TestWebhookUrl));

        var clearUpdate = new DigestSettingsUpdate(
            Enabled: true,
            Cadence: DigestCadence.Weekly,
            SendDayOfWeek: DayOfWeek.Saturday,
            SendHourLocal: 7,
            WebhookProvided: true,
            WebhookUrl: "");
        await _service.UpdateAsync(H1, clearUpdate);

        var view = await _service.GetAsync(H1);
        view.HasWebhook.Should().BeFalse("empty-string webhook must clear the stored value");

        await using var ctx = new ApplicationDbContext(_options);
        var row = await ctx.ChoreDigestSettings.SingleAsync(s => s.HouseholdId == H1);
        row.WebhookUrlProtected.Should().BeNull();
    }

    // ── GetDecryptedWebhookAsync returns null when no webhook stored ──────────

    [Fact]
    public async Task GetDecryptedWebhookAsync_NoWebhook_ReturnsNull()
    {
        await _service.UpdateAsync(H1, new DigestSettingsUpdate(
            Enabled: false,
            Cadence: DigestCadence.Weekly,
            SendDayOfWeek: DayOfWeek.Sunday,
            SendHourLocal: 18));

        var result = await _service.GetDecryptedWebhookAsync(H1);
        result.Should().BeNull();
    }

    // ── GetDecryptedWebhookAsync returns null for unknown household ───────────

    [Fact]
    public async Task GetDecryptedWebhookAsync_NoRow_ReturnsNull()
    {
        var result = await _service.GetDecryptedWebhookAsync(householdId: 999);
        result.Should().BeNull();
    }

    // ── Validation: SendHourLocal out of range throws ─────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    [InlineData(100)]
    public async Task UpdateAsync_InvalidSendHourLocal_ThrowsValidationException(int badHour)
    {
        var act = async () => await _service.UpdateAsync(H1, new DigestSettingsUpdate(
            Enabled: true,
            Cadence: DigestCadence.Weekly,
            SendDayOfWeek: DayOfWeek.Monday,
            SendHourLocal: badHour));

        await act.Should().ThrowAsync<DigestSettingsValidationException>()
            .WithMessage("*SendHourLocal*");
    }

    // ── Validation: valid boundary SendHourLocal values succeed ───────────────

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public async Task UpdateAsync_BoundarySendHourLocal_Succeeds(int hour)
    {
        var act = async () => await _service.UpdateAsync(H1, new DigestSettingsUpdate(
            Enabled: false,
            Cadence: DigestCadence.Weekly,
            SendDayOfWeek: DayOfWeek.Wednesday,
            SendHourLocal: hour));

        await act.Should().NotThrowAsync();
    }

    // ── Validation: invalid Cadence throws ────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_InvalidCadence_ThrowsValidationException()
    {
        var act = async () => await _service.UpdateAsync(H1, new DigestSettingsUpdate(
            Enabled: true,
            Cadence: (DigestCadence)999,
            SendDayOfWeek: DayOfWeek.Monday,
            SendHourLocal: 10));

        await act.Should().ThrowAsync<DigestSettingsValidationException>()
            .WithMessage("*Cadence*");
    }

    // ── Validation: invalid SendDayOfWeek throws ─────────────────────────────

    [Fact]
    public async Task UpdateAsync_InvalidSendDayOfWeek_ThrowsValidationException()
    {
        var act = async () => await _service.UpdateAsync(H1, new DigestSettingsUpdate(
            Enabled: true,
            Cadence: DigestCadence.Weekly,
            SendDayOfWeek: (DayOfWeek)99,
            SendHourLocal: 10));

        await act.Should().ThrowAsync<DigestSettingsValidationException>()
            .WithMessage("*SendDayOfWeek*");
    }

    // ── HouseholdId isolation (M1) ────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_IsolatesSettingsByHousehold()
    {
        await _service.UpdateAsync(H1, MakeUpdate(webhookUrl: TestWebhookUrl, enabled: true, hour: 9));
        await _service.UpdateAsync(H2, new DigestSettingsUpdate(
            Enabled: false,
            Cadence: DigestCadence.Weekly,
            SendDayOfWeek: DayOfWeek.Tuesday,
            SendHourLocal: 20));

        var h1 = await _service.GetAsync(H1);
        var h2 = await _service.GetAsync(H2);

        h1.Enabled.Should().BeTrue("H1 was enabled");
        h1.SendHourLocal.Should().Be(9);
        h1.HasWebhook.Should().BeTrue();

        h2.Enabled.Should().BeFalse("H2 was not enabled");
        h2.SendHourLocal.Should().Be(20);
        h2.HasWebhook.Should().BeFalse("H2 has no webhook");
    }

    [Fact]
    public async Task GetDecryptedWebhookAsync_DoesNotReturnOtherHouseholdsWebhook()
    {
        await _service.UpdateAsync(H1, MakeUpdate(webhookUrl: TestWebhookUrl));

        // H2 was never given a webhook.
        var h2Decrypted = await _service.GetDecryptedWebhookAsync(H2);
        h2Decrypted.Should().BeNull("H2 has no webhook — must not see H1's");
    }

    // ── Timestamps: CreatedAt set on insert, UpdatedAt always set ────────────

    [Fact]
    public async Task UpdateAsync_Insert_SetsCreatedAtAndUpdatedAt()
    {
        await _service.UpdateAsync(H1, MakeUpdate());

        await using var ctx = new ApplicationDbContext(_options);
        var row = await ctx.ChoreDigestSettings.SingleAsync(s => s.HouseholdId == H1);

        row.CreatedAt.Should().Be(FixedUtc, "CreatedAt must be the UTC time of insert");
        row.UpdatedAt.Should().Be(FixedUtc, "UpdatedAt must equal CreatedAt on first insert");
        row.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        row.UpdatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task UpdateAsync_SecondUpdate_AdvancesUpdatedAtButNotCreatedAt()
    {
        await _service.UpdateAsync(H1, MakeUpdate());

        // Advance the clock.
        var laterUtc = FixedUtc.AddHours(2);
        var tp = new FixedTimeProvider(FixedUtc);
        tp.SetUtcNow(laterUtc);

        var dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        var laterService = new DigestSettingsService(
            dbFactoryMock.Object,
            _dpProvider,
            tp,
            new Mock<ILogger<DigestSettingsService>>().Object);

        await laterService.UpdateAsync(H1, MakeUpdate(hour: 7));

        await using var ctx = new ApplicationDbContext(_options);
        var row = await ctx.ChoreDigestSettings.SingleAsync(s => s.HouseholdId == H1);

        row.CreatedAt.Should().Be(FixedUtc, "CreatedAt must not change on subsequent updates");
        row.UpdatedAt.Should().Be(laterUtc, "UpdatedAt must reflect the latest save time");
    }

    // ── GetAsync returns defaults for unknown household ───────────────────────

    [Fact]
    public async Task GetAsync_UnknownHousehold_ReturnsDefaults()
    {
        var view = await _service.GetAsync(householdId: 999);

        view.Should().BeEquivalentTo(new DigestSettingsView(
            Enabled: false,
            Cadence: DigestCadence.Weekly,
            SendDayOfWeek: DayOfWeek.Sunday,
            SendHourLocal: 18,
            HasWebhook: false,
            WebhookHint: null,
            LastSentAt: null));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DigestSettingsUpdate MakeUpdate(
        string? webhookUrl = null,
        bool webhookProvided = false,
        bool enabled = false,
        int hour = 18,
        DayOfWeek day = DayOfWeek.Sunday,
        DigestCadence cadence = DigestCadence.Weekly)
    {
        // If a URL is passed, automatically set webhookProvided = true.
        var provided = webhookProvided || webhookUrl is not null;
        return new DigestSettingsUpdate(
            Enabled: enabled,
            Cadence: cadence,
            SendDayOfWeek: day,
            SendHourLocal: hour,
            WebhookProvided: provided,
            WebhookUrl: webhookUrl);
    }
}
