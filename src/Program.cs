using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Oxide.CompilerServices.Common;
using Oxide.CompilerServices.Services;

namespace Oxide.CompilerServices;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Standalone HTTP validation daemon mode. Enabled via `--http`,
        // Oxide_Compiler__EnableHttpServer=true, or "Compiler": { "EnableHttpServer": true }
        // in oxide.compiler.json. This path never touches the named-pipe/game-host
        // code below - it exists purely so external tools (e.g. a PHP website) can
        // POST a .cs file and get a validation result back over HTTP.
        bool httpMode = args.Any(a => string.Equals(a, "--http", StringComparison.OrdinalIgnoreCase));

        if (!httpMode)
        {
            try
            {
                IConfigurationRoot probeConfig = new ConfigurationBuilder()
                    .AddJsonFile(Path.Combine(Constants.RootPath, "oxide.compiler.json"), optional: true)
                    .AddEnvironmentVariables("Oxide_")
                    .Build();

                httpMode = probeConfig.GetValue("Compiler:EnableHttpServer", false);
            }
            catch
            {
                // Fall back to pipe mode if the probe config can't be read for any reason -
                // the real config load inside AddServices/HttpValidationHost will surface it properly.
            }
        }

        if (httpMode)
        {
            await HttpValidationHost.RunAsync(args);
            return;
        }

        HostApplicationBuilder hostApplicationBuilder = Host.CreateApplicationBuilder(args);

        hostApplicationBuilder.Services.AddServices(hostApplicationBuilder.Configuration, args);

        using IHost host = hostApplicationBuilder.Build();
        host.Run();
    }
}
