namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The files under the host's real <c>wwwroot/uploads</c>, as of construction, so a test can see exactly which files
/// it wrote (<see cref="New"/>) and remove them afterwards (<see cref="DeleteNew"/>). Integration classes share one
/// collection and run one at a time, so another class's files never appear mid-test. Nothing may be left, let alone
/// committed, under <c>wwwroot/uploads/</c> (see <c>UploadsEndpoints</c>).
/// </summary>
public sealed class UploadFiles
{
    private readonly string _root;
    private readonly HashSet<string> _before;

    public UploadFiles(string webRootPath)
    {
        _root = Path.Combine(webRootPath, "uploads");
        _before = All().ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public string FullPath(string storedPath) => Path.Combine(_root, "..", storedPath.TrimStart('/'));

    /// <summary>Files written since construction, as stored paths (<c>/uploads/{household}/{file}</c>).</summary>
    public IReadOnlyList<string> New() =>
        All().Where(f => !_before.Contains(f))
            .Select(f => "/" + Path.GetRelativePath(Path.GetDirectoryName(_root)!, f).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    public void DeleteNew()
    {
        foreach (var file in All().Where(f => !_before.Contains(f)))
        {
            try { File.Delete(file); } catch { /* best-effort cleanup */ }
        }
    }

    private IEnumerable<string> All() =>
        Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories) : [];
}
