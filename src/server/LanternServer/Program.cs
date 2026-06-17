using LanternServer.Configuration;
using LanternServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace LanternServer;

public static class Program
{
    public static int Main(string[] args)
    {
        // Verbose toggle: Lantern:Verbose (config / appsettings.json) OR the
        // LANTERN_VERBOSE env var. When on, the minimum log level drops to Debug
        // so the RCON / A2S / HTTP / heartbeat instrumentation is emitted. Off by
        // default keeps the steady-state log to Information.
        var verbose = ResolveVerbose(args);
        var minLevel = verbose
            ? Serilog.Events.LogEventLevel.Debug
            : Serilog.Events.LogEventLevel.Information;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minLevel)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                formatter: new Serilog.Formatting.Compact.CompactJsonFormatter(),
                path: "logs/lantern-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateBootstrapLogger();

        try
        {
            PrintStartupBanner();
            Log.Information("Verbose logging {State} (min level {Level})",
                verbose ? "ON" : "off", minLevel);

            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddSerilog();

            builder.Services.Configure<LanternServerOptions>(builder.Configuration.GetSection("Lantern"));

            builder.Services.AddSingleton<InstanceIdentityProvider>();
            builder.Services.AddSingleton<HmacKeyService>();
            builder.Services.AddSingleton<PipeServerState>();
            builder.Services.AddSingleton<G2RestartCoordinator>();

            builder.Services.AddSingleton<SaveOrchestratorService>();
            builder.Services.AddSingleton<ChatService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SaveOrchestratorService>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatService>());
            builder.Services.AddHostedService<NamedPipeServerService>();
            builder.Services.AddHostedService<HeartbeatWatchdogService>();
            builder.Services.AddHostedService<SourceQueryHostedService>();
            builder.Services.AddHostedService<RconHostedService>();
            builder.Services.AddHostedService<G2ProcessSupervisorService>();
            builder.Services.AddHostedService<G2LogTailService>();
            builder.Services.AddHostedService<LanternHttpService>();
            builder.Services.AddHostedService<RosterFileWatcherService>();

            var host = builder.Build();

            // Emit the per-instance identity line now that DI has bound options.
            var opts = host.Services.GetRequiredService<IOptions<LanternServerOptions>>().Value;
            Log.Information("Instance {Instance} | gameplay:{GP} query:{QP} rcon:{RP} pipe:{Pipe}",
                opts.InstanceId, opts.GameplayPort, opts.QueryPort, opts.RconPort, opts.PipeName);
            Log.Information("Save dir: {Dir}", opts.SaveDir);
            Log.Information("Grounded 2 user dir: {Dir}", opts.GameUserDir);

            host.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "LanternServer terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Resolves the verbose flag from the same sources the host will bind later:
    // appsettings.json (Lantern:Verbose), env vars, and command-line args, plus
    // an explicit LANTERN_VERBOSE env var. Read here (before the host builds) so
    // the bootstrap logger's minimum level is right from the first line.
    private static bool ResolveVerbose(string[] args)
    {
        // Explicit env var shortcut (accepts 1/true/yes/on, case-insensitive).
        var env = Environment.GetEnvironmentVariable("LANTERN_VERBOSE");
        if (IsTruthy(env)) return true;

        try
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();
            // Bind the typed value so "true"/"false" parse correctly; default false.
            return config.GetValue("Lantern:Verbose", false);
        }
        catch
        {
            // Never let config-read trouble change the default off-state.
            return false;
        }
    }

    private static bool IsTruthy(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        v = v.Trim();
        return v.Equals("1", StringComparison.Ordinal)
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || v.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintStartupBanner()
    {
        var lanternVer = LanternVersionInfo.LanternVersion;
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var dotnetVer = Environment.Version.ToString();
        var host = Environment.MachineName;

        Log.Information("==========================================================");
        Log.Information("  Lantern Server v{Version}  (open-source Grounded 2 dedicated host)", lanternVer);
        Log.Information("  https://github.com/humangenome/Lantern");
        Log.Information("  Officially supported by https://www.survivalservers.com");
        Log.Information("----------------------------------------------------------");
        Log.Information("  host:    {Host}", host);
        Log.Information("  os:      {Os}", os);
        Log.Information("  runtime: .NET {DotNet}", dotnetVer);
        Log.Information("  g2:      build detected from host log (see [Grounded 2] lines)");
        Log.Information("==========================================================");
    }
}
