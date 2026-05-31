using Microsoft.AspNetCore.Components.Forms;

namespace FamilyCoordinationApp.Services;

public interface IImageService
{
    Task<string> SaveImageAsync(IBrowserFile file, int householdId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an uploaded <see cref="IFormFile"/> (the Minimal-API multipart path used by the chore/room
    /// island, A1). Enforces the SAME size / extension / content-type validation and traversal-safe writer as
    /// the <see cref="IBrowserFile"/> overload (M8) and returns the stored <c>/uploads/{householdId}/{guid}.{ext}</c>
    /// URL.
    /// </summary>
    Task<string> SaveImageAsync(IFormFile file, int householdId, CancellationToken cancellationToken = default);

    Task DeleteImageAsync(string imagePath, CancellationToken cancellationToken = default);
    string GetImageUrl(string? imagePath);
    Task<IEnumerable<string>> ListImagesAsync(int householdId, CancellationToken cancellationToken = default);
}

public class ImageService(
    IWebHostEnvironment environment,
    ILogger<ImageService> logger) : IImageService
{
    private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp"
    };

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp"
    };

    public Task<string> SaveImageAsync(IBrowserFile file, int householdId, CancellationToken cancellationToken = default) =>
        SaveValidatedAsync(
            file.Size,
            file.Name,
            file.ContentType,
            (maxSize, ct) => file.OpenReadStream(maxSize, ct),
            householdId,
            cancellationToken);

    public Task<string> SaveImageAsync(IFormFile file, int householdId, CancellationToken cancellationToken = default) =>
        SaveValidatedAsync(
            file.Length,
            file.FileName,
            file.ContentType,
            (_, _) => file.OpenReadStream(),
            householdId,
            cancellationToken);

    /// <summary>
    /// Shared validation + traversal-safe writer for both upload overloads (<see cref="IBrowserFile"/> and
    /// <see cref="IFormFile"/>). Enforces identical size / extension / content-type rules so neither path can
    /// be a weaker door (M8); the only per-overload difference is how the source stream is opened.
    /// </summary>
    private async Task<string> SaveValidatedAsync(
        long size,
        string fileName,
        string contentType,
        Func<long, CancellationToken, Stream> openReadStream,
        int householdId,
        CancellationToken cancellationToken)
    {
        // Validate file
        if (size > MaxFileSize)
        {
            throw new InvalidOperationException($"File size exceeds maximum allowed size of {MaxFileSize / 1024 / 1024} MB.");
        }

        var extension = Path.GetExtension(fileName);
        if (!AllowedExtensions.Contains(extension))
        {
            throw new InvalidOperationException($"File type '{extension}' is not allowed. Allowed types: {string.Join(", ", AllowedExtensions)}");
        }

        if (!AllowedContentTypes.Contains(contentType))
        {
            throw new InvalidOperationException($"Content type '{contentType}' is not allowed.");
        }

        // Generate unique filename
        var trustedFileName = $"{Guid.NewGuid()}{extension}";
        var uploadsPath = Path.Combine(environment.WebRootPath, "uploads", householdId.ToString());

        // Ensure directory exists
        Directory.CreateDirectory(uploadsPath);

        var filePath = Path.Combine(uploadsPath, trustedFileName);

        // Stream file directly to filesystem (not into memory)
        try
        {
            await using var stream = openReadStream(MaxFileSize, cancellationToken);
            await using var fs = new FileStream(filePath, FileMode.Create);
            await stream.CopyToAsync(fs, cancellationToken);

            logger.LogInformation("Saved image {FileName} for household {HouseholdId}", trustedFileName, householdId);

            // Return relative URL path
            return $"/uploads/{householdId}/{trustedFileName}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save image for household {HouseholdId}", householdId);

            // Clean up partial file if it exists
            if (File.Exists(filePath))
            {
                try { File.Delete(filePath); } catch { /* ignore cleanup errors */ }
            }

            throw;
        }
    }

    public Task DeleteImageAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return Task.CompletedTask;

        // Convert URL path to filesystem path
        // imagePath format: /uploads/{householdId}/{filename}
        var relativePath = imagePath.TrimStart('/');
        var fullPath = Path.GetFullPath(Path.Combine(environment.WebRootPath, relativePath));

        // Prevent path traversal attacks - ensure resolved path is within WebRootPath
        if (!fullPath.StartsWith(environment.WebRootPath, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Path traversal attempt blocked: {ImagePath}", imagePath);
            return Task.CompletedTask;
        }

        if (File.Exists(fullPath))
        {
            try
            {
                File.Delete(fullPath);
                logger.LogInformation("Deleted image at {Path}", imagePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete image at {Path}", imagePath);
                // Don't throw - image deletion is not critical
            }
        }

        return Task.CompletedTask;
    }

    public string GetImageUrl(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            // Return placeholder based on recipe type (handled in component)
            return string.Empty;
        }

        return imagePath;
    }

    public Task<IEnumerable<string>> ListImagesAsync(int householdId, CancellationToken cancellationToken = default)
    {
        var uploadsPath = Path.Combine(environment.WebRootPath, "uploads", householdId.ToString());

        if (!Directory.Exists(uploadsPath))
        {
            return Task.FromResult<IEnumerable<string>>(Array.Empty<string>());
        }

        var images = Directory.EnumerateFiles(uploadsPath)
            .Where(f => AllowedExtensions.Contains(Path.GetExtension(f)))
            .Select(f => $"/uploads/{householdId}/{Path.GetFileName(f)}")
            .OrderByDescending(f => f) // Newest first (GUIDs sort roughly by creation time)
            .ToList();

        return Task.FromResult<IEnumerable<string>>(images);
    }
}
