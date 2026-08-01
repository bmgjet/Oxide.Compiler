# Oxide.Compiler — HTTP validation daemon (fork)

This is a fork of [OxideMod/Oxide.Compiler](https://github.com/OxideMod/Oxide.Compiler)
that adds a second run mode alongside the original one:

- **Pipe mode** (unchanged): connects to a named pipe and is spawned/managed by
  a real Oxide-loaded game server (Rust, etc). All original files
  (`EntryPointService`, `AppHostService`, `MessageBrokerService`,
  `Common/DependencyInjection.cs`) are untouched.
- **HTTP mode** (new): a standalone daemon, started with `--http`, that exposes
  `POST /validate` so an external system (e.g. a PHP website) can submit a
  single `.cs` file and get back a structured compile result. No named pipe,
  no parent game-server process required.

The two modes share the exact same Roslyn compilation code
(`Services/CompilationService.cs`, `Common/OxideResolver.cs`), so diagnostics,
language-version handling, and merged-assembly resolution behave identically
to the real compiler used by Oxide-loaded servers.

The project already targets `net10.0` (see `src/Oxide.Compiler.csproj`); this
fork just adds `linux-arm64` (and `linux-x64`/`win-x64`) as publish targets and
an `Microsoft.AspNetCore.App` `FrameworkReference` so the console app can host
Kestrel without switching to the Web SDK.

## What changed

New files:
- `src/Services/HttpValidationHost.cs` — the `--http` entry point (Kestrel +
  minimal API endpoints), reusing `ICompilationService`/`OxideResolver` as-is.
- `src/Services/ReferenceLibraryService.cs` — loads and caches every `*.dll`
  from a references directory once at startup (and on `/reload-references`).
- `src/Types/Configuration/HttpConfiguration.cs` — daemon settings.
- `src/Types/Validation/ValidationDtos.cs` — the JSON response shape for
  `/validate`, kept separate from the internal named-pipe wire types.
- `src/oxide.compiler.json.example`, `deploy/oxide-compiler-validator.service`,
  `deploy/php-example/validate_plugin.php` — config/deploy/client examples.

Modified files:
- `src/Program.cs` — branches to `HttpValidationHost.RunAsync` when `--http`
  is passed (or `Compiler:EnableHttpServer` is set via config/env); otherwise
  runs exactly as before.
- `src/Common/Constants.cs` — added CLI switch mappings for the new HTTP
  options (`--http-address`, `--http-port`, `--http-key`, etc.).
- `src/Oxide.Compiler.csproj` — added `RuntimeIdentifiers` and the ASP.NET Core
  `FrameworkReference`.

## Reference assemblies (needed for real Oxide plugin validation)

You asked for **full plugin validation** (base classes like `RustPlugin`
resolving correctly), not just "does this parse as C#". That means the
compiler needs the same reference assemblies the real Oxide-loaded game host
would supply: `Oxide.References.dll` plus the game's managed assemblies
(e.g. `Assembly-CSharp.dll` and friends from a Rust dedicated server install).

`ReferenceLibraryService` loads every `*.dll` in one directory
(`Http:ReferencesPath`, default falls back to `Path:Libraries`, which itself
defaults to the executable's own directory) into memory once at startup, and
every `/validate` call gets that same reference set. To populate it:

1. Install/update Oxide against a real dedicated server once (any platform is
   fine for this step — it's just to obtain the DLLs).
2. Copy `Oxide.References.dll` and the game's managed assembly folder
   (e.g. `RustDedicated_Data/Managed/*.dll`) into the directory you set as
   `Http:ReferencesPath`.
3. Call `POST /reload-references` (or restart the daemon) whenever you update
   the game or Oxide, so the cached reference set stays in sync.

Because these are Facepunch/game-specific binaries, I can't include them in
this fork — that part is on your end to source from your own server install.

## Building for .NET 10 / linux-arm64

The project builds the same way as upstream, just with a different `-r`:

```bash
cd src
dotnet publish -r linux-arm64 -c Release \
  --self-contained \
  --p:PublishSingleFile=true \
  --p:PublishReadyToRun=true \
  /p:Version=2.0.0
```

This mirrors the existing `linux-x64` CI step (see `.github/workflows/build.yml`)
— self-contained + ReadyToRun + single-file, **not** Native AOT. (Upstream only
uses `PublishAot=true` for the `win-x64` build; ASP.NET Core's reflection-based
JSON serialization used here isn't a good fit for trimming/AOT, so HTTP mode
intentionally skips it.)

Output binary: `src/bin/Release/net10.0/linux-arm64/publish/Oxide.Compiler`

Notes on producing this on an actual arm64 target:
- Easiest: install the .NET 10 SDK directly on your arm64 host and run the
  command above natively (no cross-compilation concerns).
- From x64 CI: GitHub Actions' `ubuntu-24.04-arm` runner (or any arm64 runner)
  avoids R2R cross-compilation issues; add a build-arm64 job alongside the
  existing Windows job in `.github/workflows/build.yml` if you want this in CI.

## Running

```bash
cd /opt/oxide-compiler
cp oxide.compiler.json.example oxide.compiler.json   # edit ApiKey, ReferencesPath
./Oxide.Compiler --http
```

Or without editing the JSON file:

```bash
./Oxide.Compiler --http-key "$(openssl rand -hex 32)" \
  --http-address 127.0.0.1 --http-port 5085 \
  --references /opt/oxide-compiler/references
```

A `systemd` unit is provided at `deploy/oxide-compiler-validator.service` —
run it as a dedicated, unprivileged user with no network egress needed.

## HTTP API

All endpoints are on the loopback address you configure. **Do not expose this
port publicly** — compiling arbitrary code isn't itself dangerous (Roslyn
compiles, it doesn't execute the plugin), but the service has no reason to be
reachable from outside your own web server, so keep it on `127.0.0.1` and put
the shared-secret `X-Api-Key` header in front of it as defense in depth.

### `GET /health`
```json
{ "status": "ok", "referenceAssemblies": 214 }
```

### `POST /reload-references`
Re-scans the references directory. Call after updating game/Oxide assemblies.

### `POST /validate`
`multipart/form-data` with:
- `file` (required) — the `.cs` file being validated.
- `languageVersion` (optional) — `Latest`, `Preview`, `V14`, `V13`, ... default `Latest`.
- `preprocessor` (optional) — comma-separated preprocessor symbols.

Response (`200`, or `504` on timeout with the same shape):
```json
{
  "success": false,
  "fileName": "MyPlugin.cs",
  "elapsedMilliseconds": 812,
  "errors": [
    { "message": "The name 'Foo' does not exist in the current context", "file": "MyPlugin", "line": 42, "position": 9 }
  ]
}
```

See `deploy/php-example/validate_plugin.php` for a complete PHP 8.5 client
(uses `CURLFile` multipart upload, no base64 bloat).

## Operational notes

- **Concurrency**: `OxideResolver` (the Roslyn metadata resolver) is registered
  as a singleton and clears its cache after every compile job — matching how
  upstream expects one job in flight at a time over the pipe. `/validate`
  serializes compile jobs behind a semaphore for this reason; if you need
  higher throughput, the safer path is running multiple daemon processes on
  different ports behind your PHP app, not making the resolver concurrent.
- **Timeouts**: `Http:TimeoutSeconds` (default 30) bounds how long a single
  compile job can run before it's cancelled and reported back as a `504`.
- **Upload size**: `Http:MaxUploadBytes` (default 5 MB) is enforced by Kestrel
  and the form parser.
