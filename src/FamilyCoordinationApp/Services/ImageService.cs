namespace FamilyCoordinationApp.Services;

public interface IImageService
{
    /// <summary>
    /// Saves an uploaded <see cref="IFormFile"/> (the Minimal-API multipart path). Enforces size / extension /
    /// content-type validation via the traversal-safe writer and returns the stored
    /// <c>/uploads/{householdId}/{guid}.{ext}</c> URL.
    /// </summary>
    Task<string> SaveImageAsync(IFormFile file, int householdId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Duplicate a STORED upload (<c>/uploads/{sourceHouseholdId}/{file}</c>) into another household's
    /// directory, returning the new <c>/uploads/{targetHouseholdId}/{guid}.{ext}</c> path — or null when the
    /// source is not that household's stored upload or the file is missing. The recipe-copy path uses this so
    /// a copied recipe owns its own file instead of permanently referencing the origin household's — the
    /// uploads gate's forward fix (duplicate the FILE, never widen the gate).
    /// </summary>
    Task<string?> CopyImageAsync(string sourceImagePath, int sourceHouseholdId, int targetHouseholdId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a stored upload. <paramref name="householdId"/> is the tenant boundary, not a hint: the path is
    /// resolved and must land inside <c>wwwroot/uploads/{householdId}/</c> or the delete is refused. A path that
    /// is traversal-free but belongs to another household (<c>/uploads/{other}/x.jpg</c>) is a legitimate
    /// filesystem path and the old WebRootPath-only guard accepted it.
    /// </summary>
    Task DeleteImageAsync(string imagePath, int householdId, CancellationToken cancellationToken = default);
    string GetImageUrl(string? imagePath);
    Task<IEnumerable<string>> ListImagesAsync(int householdId, CancellationToken cancellationToken = default);
}

public class ImageService(
    IWebHostEnvironment environment,
    ILogger<ImageService> logger) : IImageService
{
    private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// The extensions this app accepts on upload. <c>UploadsEndpoints.ContentTypes</c> must be able to serve
    /// every one of them; <c>UploadsContentTypeTests</c> locks the two sets together.
    /// </summary>
    internal static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp"
    };

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp"
    };

    public Task<string> SaveImageAsync(IFormFile file, int householdId, CancellationToken cancellationToken = default) =>
        SaveValidatedAsync(
            file.Length,
            file.FileName,
            file.ContentType,
            (_, _) => file.OpenReadStream(),
            householdId,
            cancellationToken);

    /// <summary>
    /// Validation + traversal-safe writer behind <see cref="SaveImageAsync(IFormFile, int, CancellationToken)"/>.
    /// Takes the stream opener as a parameter so a second upload path could reuse it without relaxing the
    /// size / extension / content-type rules (M8).
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
        // Same helper the delete guard and the read gate use — the write path and the boundary check cannot
        // disagree about where a household's uploads live.
        var uploadsPath = UploadRootFor(environment.WebRootPath, householdId);

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

    public Task DeleteImageAsync(string imagePath, int householdId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return Task.CompletedTask;

        // Convert URL path to filesystem path
        // imagePath format: /uploads/{householdId}/{filename}
        // Resolution itself can throw: PhotoPath rows written before the ImagePathPolicy write boundary
        // (quest b0edfd94) were stored verbatim from the request body and are replayed here by
        // delete-on-replace — e.g. an embedded NUL makes Path.GetFullPath throw, which would surface
        // as a 500 on an otherwise-valid update. A path the OS will not parse is a path we refuse, not one we propagate.
        string fullPath;
        try
        {
            var relativePath = imagePath.TrimStart('/');
            fullPath = Path.GetFullPath(Path.Combine(environment.WebRootPath, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            logger.LogWarning(ex, "Blocked delete of an unresolvable image path for household {HouseholdId}", householdId);
            return Task.CompletedTask;
        }

        // The tenant boundary: the resolved path must be inside THIS household's upload directory. This
        // subsumes the old WebRootPath-only traversal guard (the household directory is inside WebRootPath)
        // and additionally refuses a traversal-free cross-household path.
        var householdRoot = UploadRootFor(environment.WebRootPath, householdId);
        if (!IsWithinDirectory(householdRoot, fullPath))
        {
            logger.LogWarning(
                "Blocked delete of {ImagePath}: outside household {HouseholdId}'s upload directory",
                imagePath,
                householdId);
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

    public async Task<string?> CopyImageAsync(
        string sourceImagePath, int sourceHouseholdId, int targetHouseholdId, CancellationToken cancellationToken = default)
    {
        var prefix = $"/uploads/{sourceHouseholdId}/";
        if (string.IsNullOrWhiteSpace(sourceImagePath)
            || !sourceImagePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var sourceRoot = UploadRootFor(environment.WebRootPath, sourceHouseholdId);
        string sourceFull;
        try
        {
            sourceFull = Path.GetFullPath(Path.Combine(sourceRoot, sourceImagePath[prefix.Length..]));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Refused image copy of unparseable path {ImagePath}", sourceImagePath);
            return null;
        }

        // Same tenant boundary as delete: the resolved source must be inside the SOURCE household's directory.
        if (!IsWithinDirectory(sourceRoot, sourceFull) || !File.Exists(sourceFull)) return null;

        var targetRoot = UploadRootFor(environment.WebRootPath, targetHouseholdId);
        Directory.CreateDirectory(targetRoot);
        var newName = $"{Guid.NewGuid()}{Path.GetExtension(sourceFull).ToLowerInvariant()}";
        var targetFull = Path.Combine(targetRoot, newName);

        try
        {
            await using var source = File.OpenRead(sourceFull);
            await using var target = File.Create(targetFull);
            await source.CopyToAsync(target, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // File.Exists above is check-then-use: the source can vanish between the check and the open
            // (delete-on-replace on a row referencing the same file). Missing-at-open is the same contract
            // as missing-at-check — return null, never a throw the copy endpoint would surface as a 500.
            try { File.Delete(targetFull); } catch { /* best-effort partial-file cleanup */ }
            return null;
        }

        logger.LogInformation(
            "Copied image {Source} to household {TargetHouseholdId} as {NewName}",
            sourceImagePath, targetHouseholdId, newName);
        return $"/uploads/{targetHouseholdId}/{newName}";
    }

    /// <summary>
    /// The one directory a household's uploads may live in: <c>{webRoot}/uploads/{householdId}</c>. Shared with
    /// the read gate (<c>UploadsEndpoints</c>) so the write side and the serve side cannot drift apart.
    /// </summary>
    internal static string UploadRootFor(string webRootPath, int householdId) =>
        Path.GetFullPath(Path.Combine(webRootPath, "uploads", householdId.ToString()));

    /// <summary>
    /// True when <paramref name="candidateFullPath"/> is a file inside <paramref name="directoryFullPath"/>.
    /// The trailing separator matters: a bare <c>StartsWith</c> also accepts a SIBLING whose name merely starts
    /// with the directory's name (<c>…/uploads/1</c> vs <c>…/uploads/12</c>) — which, with household ids in the
    /// path, is exactly a cross-tenant match.
    /// </summary>
    internal static bool IsWithinDirectory(string directoryFullPath, string candidateFullPath)
    {
        var root = directoryFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        return candidateFullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
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
        // Same helper as the write path, the delete guard and the read gate — one definition of where a
        // household's uploads live, so a change to the layout cannot leave this one behind.
        var uploadsPath = UploadRootFor(environment.WebRootPath, householdId);

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
