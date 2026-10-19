using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;

namespace FamilyCoordinationApp.Tests.Services;

public class IdGenerationHelperTests
{
    private readonly Mock<ILogger> _loggerMock;

    public IdGenerationHelperTests()
    {
        _loggerMock = new Mock<ILogger>();
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_SucceedsOnFirstAttempt()
    {
        // Arrange
        var attemptCount = 0;
        var expectedResult = "success";

        // Act
        var result = await FamilyCoordinationApp.Services.IdGenerationHelper.ExecuteWithRetryAsync(
            async (attempt) =>
            {
                attemptCount = attempt;
                await Task.CompletedTask;
                return expectedResult;
            },
            _loggerMock.Object,
            "TestEntity");

        // Assert
        result.Should().Be(expectedResult);
        attemptCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_RetriesOnUniqueConstraintViolation()
    {
        // Arrange
        var attemptCount = 0;
        var expectedResult = "success after retry";

        // Act
        var result = await FamilyCoordinationApp.Services.IdGenerationHelper.ExecuteWithRetryAsync(
            async (attempt) =>
            {
                attemptCount = attempt;
                await Task.CompletedTask;

                // Fail on first two attempts, succeed on third
                if (attempt < 3)
                {
                    throw CreateUniqueViolationException();
                }

                return expectedResult;
            },
            _loggerMock.Object,
            "TestEntity");

        // Assert
        result.Should().Be(expectedResult);
        attemptCount.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_ThrowsAfterMaxRetries()
    {
        // Arrange
        var attemptCount = 0;

        // Act
        var act = async () => await FamilyCoordinationApp.Services.IdGenerationHelper.ExecuteWithRetryAsync<string>(
            async (attempt) =>
            {
                attemptCount = attempt;
                await Task.CompletedTask;

                // Always fail with unique violation
                throw CreateUniqueViolationException();
            },
            _loggerMock.Object,
            "TestEntity");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to generate unique ID*TestEntity*3 attempts*");

        attemptCount.Should().Be(3); // Should have tried exactly 3 times
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_DoesNotRetryOnOtherExceptions()
    {
        // Arrange
        var attemptCount = 0;

        // Act
        var act = async () => await FamilyCoordinationApp.Services.IdGenerationHelper.ExecuteWithRetryAsync<string>(
            async (attempt) =>
            {
                attemptCount = attempt;
                await Task.CompletedTask;

                // Throw a non-unique-violation exception
                throw new InvalidOperationException("Some other error");
            },
            _loggerMock.Object,
            "TestEntity");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Some other error");

        attemptCount.Should().Be(1); // Should have only tried once
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_DoesNotRetryOnNonPostgresDbUpdateException()
    {
        // Arrange
        var attemptCount = 0;

        // Act
        var act = async () => await FamilyCoordinationApp.Services.IdGenerationHelper.ExecuteWithRetryAsync<string>(
            async (attempt) =>
            {
                attemptCount = attempt;
                await Task.CompletedTask;

                // Throw a DbUpdateException without PostgresException inner
                throw new DbUpdateException("Generic DB error", new Exception("Inner exception"));
            },
            _loggerMock.Object,
            "TestEntity");

        // Assert
        await act.Should().ThrowAsync<DbUpdateException>()
            .WithMessage("Generic DB error");

        attemptCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_ReturnsCorrectlyTypedResult()
    {
        // Arrange
        var expectedEntity = new TestEntity { Id = 42, Name = "Test" };

        // Act
        var result = await FamilyCoordinationApp.Services.IdGenerationHelper.ExecuteWithRetryAsync(
            async (attempt) =>
            {
                await Task.CompletedTask;
                return expectedEntity;
            },
            _loggerMock.Object,
            "TestEntity");

        // Assert
        result.Should().BeOfType<TestEntity>();
        result.Id.Should().Be(42);
        result.Name.Should().Be("Test");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_PassesAttemptNumberCorrectly()
    {
        // Arrange
        var recordedAttempts = new List<int>();

        // Act
        await FamilyCoordinationApp.Services.IdGenerationHelper.ExecuteWithRetryAsync(
            async (attempt) =>
            {
                recordedAttempts.Add(attempt);
                await Task.CompletedTask;

                if (attempt < 3)
                {
                    throw CreateUniqueViolationException();
                }

                return "done";
            },
            _loggerMock.Object,
            "TestEntity");

        // Assert
        recordedAttempts.Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Theory]
    [InlineData("IX_Categories_HouseholdId_Name", "A category with that name already exists.")]
    [InlineData("IX_Users_Email", "This conflicts with something that already exists. Reload and try again.")]
    [InlineData(null, "This conflicts with something that already exists. Reload and try again.")]
    public async Task ExecuteWithRetryAsync_AViolationARetryCannotCure_IsOneAttemptAndAConflict(string? constraint, string message)
    {
        // Quest d9da6ca4: only an allocated key (or the meal-plan week) is retried; any other unique constraint names a
        // row that will still be there on the re-run.
        var attemptCount = 0;

        var act = async () => await FamilyCoordinationApp.Services.IdGenerationHelper.ExecuteWithRetryAsync<string>(
            async (attempt) =>
            {
                attemptCount = attempt;
                await Task.CompletedTask;
                throw CreateUniqueViolationException(constraint);
            },
            _loggerMock.Object,
            "TestEntity");

        var thrown = await act.Should().ThrowExactlyAsync<FamilyCoordinationApp.Services.ConflictException>();
        thrown.Which.ClientMessage.Should().Be(message);
        thrown.Which.InnerException.Should().BeOfType<DbUpdateException>();
        attemptCount.Should().Be(1);
    }

    [Theory]
    [InlineData("PK_Rooms")]
    [InlineData("IX_MealPlans_HouseholdId_WeekStartDate")]
    public async Task ExecuteWithRetryAsync_ARetryableConstraint_IsRetriedToTheLimit(string constraint)
    {
        var attemptCount = 0;

        var act = async () => await FamilyCoordinationApp.Services.IdGenerationHelper.ExecuteWithRetryAsync<string>(
            async (attempt) =>
            {
                attemptCount = attempt;
                await Task.CompletedTask;
                throw CreateUniqueViolationException(constraint);
            },
            _loggerMock.Object,
            "TestEntity");

        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        attemptCount.Should().Be(3);
    }

    /// <summary>
    /// Creates a DbUpdateException that simulates a PostgreSQL unique constraint violation (23505) on
    /// <paramref name="constraintName"/> (an allocated key's primary key by default, which the helper retries).
    /// </summary>
    private static DbUpdateException CreateUniqueViolationException(string? constraintName = "PK_Recipes")
    {
        var postgresException = CreatePostgresException("23505", constraintName);
        return new DbUpdateException("Unique constraint violation", postgresException);
    }

    /// <summary>Creates a PostgresException with the specified SQL state code and constraint name.</summary>
    private static PostgresException CreatePostgresException(string sqlState, string? constraintName) =>
        new(
            messageText: "duplicate key value violates unique constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState,
            constraintName: constraintName);

    private class TestEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
