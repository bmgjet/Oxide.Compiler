namespace Oxide.CompilerServices.Types.Configuration;

/// <summary>
/// Settings for the standalone HTTP validation daemon (`--http` mode).
/// Bound from the "Http" section of oxide.compiler.json, Oxide_Http__* env vars,
/// or --http-address / --http-port / --http-key / --http-max-upload-bytes / --http-timeout-seconds.
/// </summary>
public class HttpConfiguration
{
    /// <summary>
    /// Address Kestrel binds to. Keep this on loopback (127.0.0.1) unless the daemon
    /// sits behind a firewall/reverse proxy you control - this endpoint compiles
    /// whatever it is given and should never be exposed directly to the internet.
    /// </summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5085;

    /// <summary>
    /// Shared secret PHP must send as the "X-Api-Key" header. Leave empty to disable
    /// (not recommended - only safe if ListenAddress is loopback-only and no other
    /// local user/process on the box can reach it).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Max accepted upload size for a single .cs file, in bytes.</summary>
    public long MaxUploadBytes { get; set; } = 5_000_000;

    /// <summary>Hard cap on how long a single compile job may run before being cancelled.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Directory to scan for the "main" branch's reference assemblies
    /// (Oxide.References.dll, game managed assemblies, other plugin dlls, etc.)
    /// needed to resolve types like RustPlugin. Defaults to a "Main-references"
    /// folder next to the compiler executable if not set - that folder is
    /// created automatically if missing (initially empty; populate it and hit
    /// POST /reload-references?branch=main).
    /// </summary>
    public string? MainReferencesPath { get; set; }

    /// <summary>
    /// Same as <see cref="MainReferencesPath"/> but for the "staging" branch.
    /// Defaults to a "Staging-references" folder next to the compiler executable.
    /// </summary>
    public string? StagingReferencesPath { get; set; }

    /// <summary>
    /// Filenames (case-insensitive, no path) to skip when scanning
    /// <see cref="ReferencesPath"/>. Game "Managed" folders (e.g. Rust's) typically
    /// ship an old Mono-era mscorlib.dll/System*.dll set alongside the assemblies a
    /// plugin actually needs. Loading those next to the compiler's own modern .NET
    /// standard library (StdLib=true) gives Roslyn two conflicting definitions of
    /// System.Object/System.Void/etc. and every compile fails with "Predefined type
    /// ... is not defined or imported". Oxide.References.dll already provides the
    /// facade surface plugins need, so the raw legacy BCL dlls are excluded by
    /// default. Add to this list (via oxide.compiler.json) if you hit similar
    /// duplicate/ambiguous predefined-type errors from some other assembly.
    /// </summary>
    public string[] ExcludedReferenceAssemblies { get; set; } =
    [
        "mscorlib.dll",
        "System.dll",
        "System.Core.dll",
        "System.Data.dll",
        "System.Drawing.dll",
        "System.Numerics.dll",
        "System.Runtime.Serialization.dll",
        "System.Xml.dll",
        "System.Xml.Linq.dll",
        "System.Configuration.dll",
        "System.Web.dll",
        "System.ServiceModel.dll",
        "System.Transactions.dll",
        "Microsoft.CSharp.dll",
        "netstandard.dll",
        "WindowsBase.dll",
        "System.ComponentModel.Composition.dll"
    ];
}
