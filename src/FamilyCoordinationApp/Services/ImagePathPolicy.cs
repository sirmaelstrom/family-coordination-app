namespace FamilyCoordinationApp.Services;

/// <summary>
/// The write-boundary policy for <c>ImagePath</c>/<c>PhotoPath</c> (quest b0edfd94). A stored path may be
/// exactly one of: nothing, an absolute http(s) URL (scraped and copied recipes carry these), or an upload
/// belonging to the WRITING household — <c>/uploads/{ownHouseholdId}/{fileName}</c> with a plain file name.
/// Everything else is refused at the endpoint with a 400.
/// <para>What this closes: a row pointing at ANOTHER household's <c>/uploads/</c> directory (the
/// attacker-authored-predicate hazard the uploads gate must defend against), traversal fragments, and
/// OS-unparseable strings (an embedded NUL made <c>Path.GetFullPath</c> throw inside delete-on-replace,
/// surfacing a 500 on an otherwise-valid update). Rows written before this policy existed are not
/// retro-validated — the uploads gate and <c>DeleteImageAsync</c>'s own guards remain the enforcement for
/// legacy data, which is why neither loosens on account of this policy.</para>
/// </summary>
public static class ImagePathPolicy
{
    /// <summary>Column cap is varchar(500); refuse over-length rather than let the DB truncate or throw.</summary>
    private const int MaxLength = 500;

    /// <summary>
    /// Validate + normalize a client-supplied image path for <paramref name="householdId"/>.
    /// Returns false when the value is not one of the accepted shapes; <paramref name="normalized"/> is
    /// null for blank input, else the trimmed value.
    /// </summary>
    public static bool TryNormalize(string? raw, int householdId, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var value = raw.Trim();
        if (value.Length > MaxLength) return false;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            normalized = value;
            return true;
        }

        var ownPrefix = $"/uploads/{householdId}/";
        if (value.StartsWith(ownPrefix, StringComparison.Ordinal)
            && IsPlainFileName(value[ownPrefix.Length..]))
        {
            normalized = value;
            return true;
        }

        return false;
    }

    /// <summary>A single path segment the OS will parse: no separators, no traversal, no control chars.</summary>
    private static bool IsPlainFileName(string name) =>
        name.Length is > 0 and <= 260
        && !name.Contains('/')
        && !name.Contains('\\')
        && !name.Contains("..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
