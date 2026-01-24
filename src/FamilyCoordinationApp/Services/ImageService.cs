using Microsoft.AspNetCore.Components.Forms;

namespace FamilyCoordinationApp.Services;

public interface IImageService
{
    Task<string> SaveImageAsync(IBrowserFile file, int householdId, CancellationToken cancellationToken = default);
    Task DeleteImageAsync(string imagePath, CancellationToken cancellationToken = default);
    string GetImageUrl(string? imagePath);
}

public class ImageService : IImageService
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ImageService> _logger;
    private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp"
    };

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp"
    };

    public ImageService(IWebHostEnvironment environment, ILogger<ImageService> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public async Task<string> SaveImageAsync(IBrowserFile file, int householdId, CancellationToken cancellationToken = default)
    {
        // Validate file
        if (file.Size > MaxFileSize)
        {
            throw new InvalidOperationException($"File size exceeds maximum allowed size of {MaxFileSize / 1024 / 1024} MB.");
        }

        var extension = Path.GetExtension(file.Name);
        if (!AllowedExtensions.Contains(extension))
        {
            throw new InvalidOperationException($"File type '{extension}' is not allowed. Allowed types: {string.Join(", ", AllowedExtensions)}");
        }

        if (!AllowedContentTypes.Contains(file.ContentType))
        {
            throw new InvalidOperationException($"Content type '{file.ContentType}' is not allowed.");
        }

        // Generate unique filename
        var trustedFileName = $"{Guid.NewGuid()}{extension}";
        var uploadsPath = Path.Combine(_environment.WebRootPath, "uploads", householdId.ToString());

        // Ensure directory exists
        Directory.CreateDirectory(uploadsPath);

        var filePath = Path.Combine(uploadsPath, trustedFileName);

        // Stream file directly to filesystem (not into memory)
        try
        {
            await using var stream = file.OpenReadStream(MaxFileSize, cancellationToken);
            await using var fs = new FileStream(filePath, FileMode.Create);
            await stream.CopyToAsync(fs, cancellationToken);

            _logger.LogInformation("Saved image {FileName} for household {HouseholdId}", trustedFileName, householdId);

            // Return relative URL path
            return $"/uploads/{householdId}/{trustedFileName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save image for household {HouseholdId}", householdId);

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
        var fullPath = Path.Combine(_environment.WebRootPath, relativePath);

        if (File.Exists(fullPath))
        {
            try
            {
                File.Delete(fullPath);
                _logger.LogInformation("Deleted image at {Path}", imagePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete image at {Path}", imagePath);
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
}
