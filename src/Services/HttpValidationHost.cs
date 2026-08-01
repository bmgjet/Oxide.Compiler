using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Oxide.CompilerServices.Common;
using Oxide.CompilerServices.Interfaces;
using Oxide.CompilerServices.Serialization;
using Oxide.CompilerServices.Types.Compilation;
using Oxide.CompilerServices.Types.Configuration;
using Oxide.CompilerServices.Types.Validation;

namespace Oxide.CompilerServices.Services;

/// <summary>
/// Standalone entry point for `--http` mode (or Compiler:EnableHttpServer=true in
/// oxide.compiler.json, which is the shipped default - just running the exe with
/// no arguments starts this). Deliberately independent of the named-pipe/game-host
/// bootstrap in Common/DependencyInjection.cs and Services/AppHostService.cs +
/// EntryPointService.cs + MessageBrokerService.cs's pipe usage - this never
/// connects a pipe and never expects a parent game-server process.
///
/// Maintains two fully independent "branches" (main/staging), each with its own
/// AppConfiguration + OxideResolver + CompilationService + ReferenceLibraryService
/// pointed at its own references folder. A single shared OxideResolver can only
/// ever point at one Libraries folder at a time (it's a singleton with a fixed
/// DirectoryConfiguration baked in), so rather than dynamically re-pointing a
/// shared instance per request (fragile, and racy without very careful locking),
/// each branch gets its own complete, isolated object graph. This also means each
/// branch's OxideResolver has its own reference cache, so there's no risk of
/// cross-branch cache bleed.
///
/// IMPORTANT: every Map* handler below is a `static` lambda that takes every
/// value it needs as a parameter (bound from DI or the request), and captures
/// nothing from RunAsync's local scope. On .NET 9/10, a *non-static* async
/// lambda used as a minimal-API handler that closes over local variables gets
/// compiled as a doubly-nested type
/// (`HttpValidationHost+&lt;&gt;c__DisplayClassN_M+&lt;&lt;RunAsync&gt;b__X&gt;d`),
/// and ASP.NET Core's endpoint-metadata reflection
/// (RouteEndpointDataSource -> Attribute.GetCustomAttributes, looking for the
/// AsyncStateMachine attribute) throws a TypeLoadException trying to resolve
/// that name at startup / first request. Keeping handlers static + capture-free
/// avoids the display class entirely (the state machine ends up singly-nested
/// instead), which sidesteps the bug. Do not "simplify" these back into
/// closures over local config/service variables.
/// </summary>
public static class HttpValidationHost
{
    // OxideResolver is not thread-safe, and CompilationService disposes its cached
    // metadata after every job. Serialize compile jobs (across BOTH branches, since
    // they still share the same process) so concurrent HTTP requests can't corrupt
    // each other's in-flight references.
    private static readonly SemaphoreSlim CompileGate = new(1, 1);

    private const string DefaultBranch = "main";

    // Pure marker type for logger category naming inside the static handlers
    // below (they can't reference the outer Program class conveniently without
    // adding a using that risks name collisions).
    private sealed class LogCategory;

    /// <summary>One branch's fully independent compiler object graph.</summary>
    public sealed class CompilerBranch(string name, ReferenceLibraryService referenceLibrary, ICompilationService compilationService)
    {
        public string Name { get; } = name;
        public ReferenceLibraryService ReferenceLibrary { get; } = referenceLibrary;
        public ICompilationService CompilationService { get; } = compilationService;
    }

    /// <summary>Minimal fixed-value IOptionsMonitor so AppConfiguration can be built per-branch without DI.</summary>
    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string> listener) => null;
    }

    public static async Task RunAsync(string[] args)
    {
        // "--http" is handled as a bare mode-select flag in Program.cs and is
        // deliberately NOT in Constants.SwitchMappings. If it were left in the
        // array passed to AddCommandLine, .NET's command-line config parser
        // would treat it as an ordinary "--key value" switch and unconditionally
        // consume the very NEXT token as its value - silently eating whatever
        // came right after it. Strip it out before parsing.
        string[] configArgs = args
            .Where(a => !string.Equals(a, "--http", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = configArgs,
            ContentRootPath = Constants.RootPath
        });

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddJsonFile(Path.Combine(Constants.RootPath, "oxide.compiler.json"), optional: true, reloadOnChange: true);
        builder.Configuration.AddEnvironmentVariables("Oxide_");
        builder.Configuration.AddCommandLine(configArgs, Constants.SwitchMappings);

        HttpConfiguration httpConfig = new();
        builder.Configuration.GetSection("Http").Bind(httpConfig);

        CompilerConfiguration compilerConfig = new();
        builder.Configuration.GetSection("Compiler").Bind(compilerConfig);

        Types.Configuration.LoggingConfiguration loggingConfig = new();
        builder.Configuration.GetSection("Logging").Bind(loggingConfig);

        string mainReferencesPath = !string.IsNullOrWhiteSpace(httpConfig.MainReferencesPath)
            ? httpConfig.MainReferencesPath
            : Path.Combine(Constants.RootPath, "Main-references");

        string stagingReferencesPath = !string.IsNullOrWhiteSpace(httpConfig.StagingReferencesPath)
            ? httpConfig.StagingReferencesPath
            : Path.Combine(Constants.RootPath, "Staging-references");

        builder.Services.AddSingleton(httpConfig);
        builder.Services.AddSingleton<IConfigurationRoot>(builder.Configuration);

        // Built lazily on first resolution (forced right after app.Build() below)
        // so each branch's loggers come from the fully configured logging pipeline.
        builder.Services.AddSingleton(sp => new Dictionary<string, CompilerBranch>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultBranch] = BuildBranch(sp, DefaultBranch, mainReferencesPath, compilerConfig, loggingConfig, httpConfig.ExcludedReferenceAssemblies),
            ["staging"] = BuildBranch(sp, "staging", stagingReferencesPath, compilerConfig, loggingConfig, httpConfig.ExcludedReferenceAssemblies)
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Parse(httpConfig.ListenAddress), httpConfig.Port);
            options.Limits.MaxRequestBodySize = httpConfig.MaxUploadBytes;
        });

        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = httpConfig.MaxUploadBytes;
        });

        WebApplication app = builder.Build();

        Dictionary<string, CompilerBranch> branches = app.Services.GetRequiredService<Dictionary<string, CompilerBranch>>();

        ILogger startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HttpValidationHost");
        startupLogger.LogInformation("Oxide.Compiler HTTP validation daemon listening on {0}:{1}", httpConfig.ListenAddress, httpConfig.Port);
        foreach (CompilerBranch branch in branches.Values)
        {
            int count = branch.ReferenceLibrary.Reload();
            startupLogger.LogInformation("Branch '{0}': {1} reference assemblies loaded from {2}",
                branch.Name, count, branch.ReferenceLibrary.ReferencesPath);
        }
        if (string.IsNullOrEmpty(httpConfig.ApiKey))
        {
            startupLogger.LogWarning("Http:ApiKey is not set - /validate is unauthenticated. Only safe on loopback.");
        }

        app.MapGet("/health", static (Dictionary<string, CompilerBranch> branches) => Results.Ok(new
        {
            status = "ok",
            branches = branches.ToDictionary(kv => kv.Key, kv => kv.Value.ReferenceLibrary.Count)
        }));

        app.MapPost("/reload-references", static (HttpRequest request, Dictionary<string, CompilerBranch> branches,
            HttpConfiguration httpConfig) =>
        {
            if (!IsAuthorized(request, httpConfig.ApiKey))
            {
                return Results.Unauthorized();
            }

            string? requestedBranch = request.Query.TryGetValue("branch", out StringValues qb) ? qb.ToString() : null;

            Dictionary<string, int> reloaded = new();
            foreach ((string key, CompilerBranch branch) in branches)
            {
                if (requestedBranch != null && !string.Equals(requestedBranch, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                reloaded[key] = branch.ReferenceLibrary.Reload();
            }

            return Results.Ok(new { reloaded });
        });

        app.MapPost("/validate", static async (HttpRequest request, IFormFile? file, Dictionary<string, CompilerBranch> branches,
            HttpConfiguration httpConfig, ILogger<LogCategory> logger, CancellationToken requestAborted) =>
        {
            if (!IsAuthorized(request, httpConfig.ApiKey))
            {
                return Results.Unauthorized();
            }

            if (file == null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "Missing form file field 'file'" });
            }

            if (!string.Equals(Path.GetExtension(file.FileName), ".cs", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Only .cs files are accepted" });
            }

            string branchKey = request.Form.TryGetValue("branch", out StringValues bv)
                ? bv.ToString().Trim()
                : DefaultBranch;

            if (string.IsNullOrEmpty(branchKey) || !branches.TryGetValue(branchKey, out CompilerBranch? branch))
            {
                branch = branches[DefaultBranch];
            }

            string languageVersionRaw = request.Form.TryGetValue("languageVersion", out StringValues lv)
                ? lv.ToString()
                : nameof(CompilerLanguageVersion.Latest);

            if (!Enum.TryParse(languageVersionRaw, true, out CompilerLanguageVersion languageVersion))
            {
                languageVersion = CompilerLanguageVersion.Latest;
            }

            string[] preprocessor = request.Form.TryGetValue("preprocessor", out StringValues pp)
                ? pp.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];

            await using MemoryStream sourceStream = new();
            await file.CopyToAsync(sourceStream, requestAborted);

            CompilerData compilerData = new()
            {
                StdLib = true,
                Debug = false,
                Target = CompilerTarget.Library,
                Version = languageVersion,
                Preprocessor = preprocessor,
                SourceFiles = [new CompilerFile(file.FileName, sourceStream.ToArray())],
                ReferenceFiles = branch.ReferenceLibrary.GetReferenceFiles()
            };

            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(httpConfig.TimeoutSeconds));
            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(requestAborted, timeoutCts.Token);

            Stopwatch stopwatch = Stopwatch.StartNew();
            await CompileGate.WaitAsync(requestAborted);
            try
            {
                CompilerMessage message = await branch.CompilationService.GetCompilationAsync(
                    Random.Shared.Next(), compilerData, linkedCts.Token);

                CompilationResult? result = null;
                if (message.Type == MessageType.Data && message.Data is { Length: > 0 })
                {
                    result = JsonSerializer.Deserialize(message.Data, CompilationResultContext.Default.CompilationResult);
                }

                bool success = message.Type == MessageType.Data
                    && (message.Errors == null || message.Errors.Count == 0)
                    && result?.Data is { Length: > 0 };

                return Results.Ok(new ValidationResponseDto
                {
                    Success = success,
                    FileName = file.FileName,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                    Errors = message.Errors?.Select(e => new ValidationErrorDto
                    {
                        Message = e.Message,
                        File = e.File,
                        Line = e.Line,
                        Position = e.Position
                    }).ToList() ?? []
                });
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return Results.Json(new ValidationResponseDto
                {
                    Success = false,
                    FileName = file.FileName,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                    Errors = [new ValidationErrorDto { Message = $"Compilation timed out after {httpConfig.TimeoutSeconds}s" }]
                }, statusCode: 504);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unhandled error validating {0}", file.FileName);
                return Results.Problem("Internal compilation error", statusCode: 500);
            }
            finally
            {
                CompileGate.Release();
            }
        }).DisableAntiforgery();

        await app.RunAsync();
    }

    private static CompilerBranch BuildBranch(IServiceProvider sp, string name, string referencesPath,
        CompilerConfiguration compilerConfig, Types.Configuration.LoggingConfiguration loggingConfig, string[] excludedAssemblies)
    {
        ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        IConfigurationRoot configRoot = sp.GetRequiredService<IConfigurationRoot>();

        ReferenceLibraryService referenceLibrary = new(loggerFactory.CreateLogger<ReferenceLibraryService>(), referencesPath, excludedAssemblies);

        DirectoryConfiguration directoryConfig = new() { Libraries = referencesPath };

        AppConfiguration appConfiguration = new(
            configRoot,
            new StaticOptionsMonitor<CompilerConfiguration>(compilerConfig),
            new StaticOptionsMonitor<DirectoryConfiguration>(directoryConfig),
            new StaticOptionsMonitor<Types.Configuration.LoggingConfiguration>(loggingConfig));

        MessageBrokerService messageBroker = new(loggerFactory.CreateLogger<MessageBrokerService>(), appConfiguration);
        OxideResolver resolver = new(loggerFactory.CreateLogger<OxideResolver>(), appConfiguration);
        CompilationService compilationService = new(loggerFactory.CreateLogger<CompilationService>(), appConfiguration, messageBroker, resolver);

        return new CompilerBranch(name, referenceLibrary, compilationService);
    }

    private static bool IsAuthorized(HttpRequest request, string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return true;
        }

        return request.Headers.TryGetValue("X-Api-Key", out StringValues provided) && provided == apiKey;
    }
}
