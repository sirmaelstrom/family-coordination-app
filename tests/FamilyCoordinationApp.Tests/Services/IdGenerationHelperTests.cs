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

    /// <summary>
    /// Creates a DbUpdateException that simulates a PostgreSQL unique constraint violation (23505).
    /// </summary>
    private static DbUpdateException CreateUniqueViolationException()
    {
        // PostgresException requires specific construction - we use reflection to set SqlState
        // since the constructor is internal. We create a mock scenario instead.
        var postgresException = CreatePostgresException("23505");
        return new DbUpdateException("Unique constraint violation", postgresException);
    }

    /// <summary>
    /// Creates a PostgresException with the specified SQL state code.
    /// This uses a workaround since PostgresException's constructor is complex.
    /// </summary>
    private static PostgresException CreatePostgresException(string sqlState)
    {
        // PostgresException can be constructed through its public constructor
        // that takes a PostgresErrorData-like structure. We'll use the serialization approach.
        // For testing, we create it via the public constructor available in Npgsql 8+

        // Use the constructor: PostgresException(string messageText, string severity, string invariantSeverity, string sqlState)
        try
        {
            return new PostgresException(
                messageText: "duplicate key value violates unique constraint",
                severity: "ERROR",
                invariantSeverity: "ERROR",
                sqlState: sqlState);
        }
        catch
        {
            // Fallback for different Npgsql versions - create via reflection or throw
            throw new InvalidOperationException($"Could not create PostgresException with SqlState {sqlState}");
        }
    }

    private class TestEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
