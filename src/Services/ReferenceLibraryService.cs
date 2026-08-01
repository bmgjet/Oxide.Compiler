using System.Reflection;
using Microsoft.Extensions.Logging;
using Oxide.CompilerServices.Types.Compilation;

namespace Oxide.CompilerServices.Services;

/// <summary>
/// Loads and caches every *.dll found under the configured references directory
/// (Oxide.References.dll, game managed assemblies such as Assembly-CSharp.dll,
/// other compiled plugins, etc.) so every /validate call can resolve the base
/// classes/types a real Oxide plugin uses (e.g. RustPlugin).
///
/// Bytes are read into memory once (at startup and on /reload-references) rather
/// than passed as file paths, because OxideResolver matches "merged assembly"
/// redirects (see Oxide.References.dll's embedded metadata) against the plain
/// file name - passing a full path would silently break that redirect.
///
/// If Http:ReferencesPath isn't set, it falls back to the compiler's own bin
/// directory (Path:Libraries) - the same folder the compiler itself runs from.
/// That means this scan would otherwise happily pick up Oxide.Compiler.dll
/// itself (and its own runtime dependency dlls) as a "reference". Since the
/// HTTP daemon's own assembly references ASP.NET Core types
/// (Microsoft.AspNetCore.Http.Abstractions.dll etc.) that don't exist as files
/// anywhere OxideResolver looks, that silently corrupts every compile with
/// "Predefined type ... is not defined" errors. The compiler's own entry
/// assembly is always excluded automatically to prevent that; you should still
/// prefer pointing Http:ReferencesPath at a dedicated folder that only
/// contains Oxide.References.dll + game/plugin assemblies.
/// </summary>
public class ReferenceLibraryService
{
    private readonly ILogger<ReferenceLibraryService> _logger;
    private readonly string _referencesPath;
    private readonly HashSet<string> _excludedFileNames;
    private readonly string? _ownAssemblyPath;
    private IReadOnlyList<CompilerFile> _cachedReferences = Array.Empty<CompilerFile>();
    private readonly object _lock = new();

    public ReferenceLibraryService(ILogger<ReferenceLibraryService> logger, string referencesPath,
        IEnumerable<string>? excludedFileNames = null)
    {
        _logger = logger;
        _referencesPath = referencesPath;
        _excludedFileNames = new HashSet<string>(excludedFileNames ?? [], StringComparer.OrdinalIgnoreCase);
        _ownAssemblyPath = Assembly.GetEntryAssembly()?.Location;
    }

    public int Count => _cachedReferences.Count;

    public string ReferencesPath => _referencesPath;

    /// <summary>
    /// Returns the cached reference set. Safe to reuse across requests - the
    /// compiler treats these as read-only input and rebuilds its own metadata
    /// view of them per compile job.
    /// </summary>
    public CompilerFile[] GetReferenceFiles()
    {
        lock (_lock)
        {
            return _cachedReferences.ToArray();
        }
    }

    public int Reload()
    {
        if (!Directory.Exists(_referencesPath))
        {
            try
            {
                Directory.CreateDirectory(_referencesPath);
                _logger.LogWarning(
                    "References path {0} did not exist - created it empty. Plugins using game/Oxide " +
                    "base classes (e.g. RustPlugin) will fail to compile until you populate it and call " +
                    "POST /reload-references.", _referencesPath);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to create references path {0}", _referencesPath);

                lock (_lock)
                {
                    _cachedReferences = Array.Empty<CompilerFile>();
                }

                return 0;
            }
        }

        List<CompilerFile> loaded = new();
        int skipped = 0;
        foreach (string path in Directory.EnumerateFiles(_referencesPath, "*.dll", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(path);
            if (_excludedFileNames.Contains(fileName) || IsOwnAssembly(path))
            {
                skipped++;
                continue;
            }

            try
            {
                byte[] data = File.ReadAllBytes(path);
                loaded.Add(new CompilerFile(fileName, data));
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to load reference assembly {0}", path);
            }
        }

        lock (_lock)
        {
            _cachedReferences = loaded;
        }

        _logger.LogInformation("Loaded {0} reference assemblies from {1} ({2} excluded)", loaded.Count, _referencesPath, skipped);
        return loaded.Count;
    }

    private bool IsOwnAssembly(string path) =>
        _ownAssemblyPath != null && string.Equals(
            Path.GetFullPath(path), Path.GetFullPath(_ownAssemblyPath), StringComparison.OrdinalIgnoreCase);
}
