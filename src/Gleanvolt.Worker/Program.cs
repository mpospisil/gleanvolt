using System.Globalization;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Settings.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting;
using Gleanvolt.Hosting.Cli;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Infrastructure.Secrets;
using Gleanvolt.Web;
using Gleanvolt.Web.Auth;
using Gleanvolt.Worker;

// The controller is culture-invariant: what it logs, publishes to MQTT and answers on the API must
// not depend on the locale of the machine it happens to run on (a cs-CZ host once turned "6.00 kWh"
// into "6,00 kWh"). Pinned here, first, for every thread the process will ever start. The web UI is
// the one exception and opts out per request -- see WebUiCultures (#290).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

// A tiny offline tool rather than a whole second entry point: the password is a secret that must
// never live in appsettings.json, and this is the only way to produce the hash that belongs in
// Web__PasswordHash instead. Takes precedence over everything below -- no configuration, no
// listening socket, just the hash on stdout.
if (args is ["hash-password", var plainPassword])
{
    Console.WriteLine(WebPasswordHasher.Hash(plainPassword));
    return 0;
}

// The default content root is the process's current directory, not the exe's own -- fine for
// `dotnet run` (already the project directory) and for the systemd unit and the container image
// (each pins or coincides with the right directory another way), but not for a single-file publish
// run through WinGet's PortableCommandAlias: that launches Links\gleanvolt.exe, a reparse-point
// symlink whose directory has none of appsettings.json, wwwroot or data/, only the real install
// directory does. AppContext.BaseDirectory and Environment.ProcessPath both return the symlink's own
// path, not the real one -- verified directly: neither is "resolved" by the OS or the runtime before
// the process sees it. FileInfo.ResolveLinkTarget is, so that is what has to run.
// Assembly.GetEntryAssembly().Location is empty only for a single-file publish, so this changes
// nothing for every other way the worker runs.
#pragma warning disable IL3000 // Read only for emptiness, as the single-file marker it always is -- never used as a path.
if (string.IsNullOrEmpty(System.Reflection.Assembly.GetEntryAssembly()?.Location))
#pragma warning restore IL3000
{
    var exePath = Environment.ProcessPath is { } processPath
        ? new FileInfo(processPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? processPath
        : AppContext.BaseDirectory;
    Directory.SetCurrentDirectory(Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory);
}

// Load secrets (e.g. Solcast__ApiKey) from an untracked .env file into the process environment
// before configuration is built, so they reach the app whether it's started via `dotnet run` or
// the VS Code debugger -- without living in any committed file. Real env vars still take priority.
DotEnv.Load(Directory.GetCurrentDirectory());

// The command-line setup (#303): setup, set-password/reset-password and discover. They read the
// configuration the service reads and save through the same path as the web wizard, and they never
// start the host. Under the .deb, the service's own environment is loaded first, so a command typed
// in a shell writes where the service looks.
if (GleanvoltCli.IsCommand(args))
{
    var serviceUser = DebEnvironment.Load(Environment.ProcessPath);

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        // The first Ctrl+C stops a device search and keeps what it found; a second one ends the process.
        e.Cancel = !stop.IsCancellationRequested;
        stop.Cancel();
    };

    var context = CliContext.ForThisHost(configuration => CliPlatform.Current(serviceUser, configuration));

    return await GleanvoltCli.RunAsync(args, context, stop.Token);
}

// Serilog swallows failures inside its own sinks. That silence is dangerous in the container: if the
// bind-mounted logs directory isn't writable by the image's non-root user, the console keeps logging
// normally and the log files simply never appear -- verified, and invisible without this line.
Serilog.Debugging.SelfLog.Enable(Console.Error);

// Serilog.Settings.Configuration normally finds "Console" and "File" in the WriteTo section by
// scanning the output directory for Serilog.Sinks.*.dll -- there is no such directory to scan once
// the worker is published single-file, and it throws instead of logging anything. Naming the sink
// assemblies here skips that scan.
var serilogAssemblies = new ConfigurationReaderOptions(
    typeof(ConsoleLoggerConfigurationExtensions).Assembly,
    typeof(FileLoggerConfigurationExtensions).Assembly);

// One pass per host. Normally exactly one: the loop only goes round again for a restart carried out
// inside the process (see the end of the loop body).
while (true)
{
    // A web host, but only sometimes a web server. The self-hosted UI (issue #44) lives in Gleanvolt.Web and
    // is hosted here, so the process needs ASP.NET's builder; when the UI is switched off the process
    // must still be exactly what it was before — a headless worker that listens on nothing. See
    // GleanvoltHostingExtensions for how that is enforced rather than merely intended.
    var builder = WebApplication.CreateBuilder(args);

    // The .deb package runs this as a systemd `Type=notify` unit (issue #205): readiness is reported to
    // systemd once the host has actually started, so `systemctl start` waits for it and a startup refusal
    // is a failed start rather than a service that looks active and exits a second later. A no-op outside
    // systemd -- it checks for the manager's environment itself -- so Docker, `dotnet run` and Windows
    // are unaffected.
    builder.Services.AddSystemd();

    builder.Logging.ClearProviders();

    builder.Services.AddSerilog(config => config
        .ReadFrom.Configuration(builder.Configuration, serilogAssemblies)
        .Enrich.FromLogContext());

    // Everything the controller is: polling, the control strategies, the session store, the Home
    // Assistant integration and the web UI. It lives in Gleanvolt.Hosting so that this file is a host and
    // nothing more -- see that assembly for why each service is registered the way it is.
    // A configuration the controller cannot run on stops it here -- headless, with the web UI off (#301);
    // with the UI on, the host starts in setup mode instead. Logged before the process ends, through a
    // logger of its own: the host's logging does not exist yet, and the log file is where a .deb or a
    // Windows install is looked into, not the console it was started from.
    WebApplication host;
    try
    {
        builder.AddGleanvolt();
        host = builder.Build();
    }
    catch (InvalidOperationException ex)
    {
        using var refusal = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration, serilogAssemblies)
            .CreateLogger();

        refusal.Fatal("Gleanvolt {Version} cannot start: {Reason}", BuildInfo.Describe(), ex.Message);

        // EX_CONFIG from sysexits.h: "something was found in an unconfigured or misconfigured state".
        // Non-zero, so systemd reports a failed start; Docker restarts it, as it always has.
        return 78;
    }

    host.UseGleanvolt();

    var log = host.Services.GetRequiredService<ILogger<Program>>();

    // First line in the log, before anything can go wrong: a log file or a `docker logs` dump is
    // otherwise untraceable to the build that produced it. A "-dev" suffix with no commit means a
    // local build rather than anything CI published.
    log.LogInformation("Gleanvolt {Version} starting.", BuildInfo.Describe());

    // What this process thinks it is: which installation, and which boxes it will be talking to (issue
    // #111). A log file that cannot answer that is a log file nobody can read six months later.
    // A value saved from /pv-system (issue #204) overrides .env, which is exactly what makes ".env isn't
    // working" otherwise inexplicable from a log. The suffix names every key the file overrode, and where it is.
    var setup = host.Services.GetRequiredService<ISetupState>();

    if (setup.SetupMode)
    {
        // Setup mode (#301): the one thing this log must make obvious, since nothing else will happen.
        log.LogWarning(
            "SETUP MODE: Gleanvolt is not configured, so the controller is NOT running -- no polling, no "
            + "charging, no recording. Open the web UI on port {Port} to finish the setup, then restart. "
            + "Missing:{Problems}",
            host.Services.GetRequiredService<IOptions<WebOptions>>().Value.Port,
            string.Concat(setup.AtStartup.MissingRequired.Select(problem => Environment.NewLine + "  - " + problem.English)));
    }
    else
    {
        log.LogInformation(
            "PV system: {System}{Overrides}.",
            host.Services.GetRequiredService<PvSystemInfo>().Describe(),
            PvSystemOverrides.DescribeLoaded(builder.Configuration));
    }

    // Where the bearer-equivalent secrets live and what protects them (issue #215). On the startup log
    // because it is the answer to "is it safe to copy the data directory?", and the person asking that is
    // usually reading a log rather than the README. Said in words that do not overclaim: nothing here is
    // proof against a local root, because the service has to come back from a restart with nobody present.
    var secrets = host.Services.GetRequiredService<ISecretStore>();
    var secretStore = host.Services.GetRequiredService<SecretStoreChoice>();

    log.LogInformation(
        "Secrets: {Protection}, because {Reason}. The data directory holds bearer-equivalent secrets; "
        + "back it up accordingly.",
        secrets.Describe(),
        secretStore.Reason);

    if (secretStore.Warning is { } secretWarning)
    {
        log.LogWarning("{Warning}", secretWarning);
    }

    // And the car, on the same terms (#124): "which installation was this, and what car did it think it
    // had" should both be answerable from a `docker logs` dump with no configuration file beside it.
    // Not in setup mode: the car may be one of the things still being set up, and a car the rules refuse
    // must not stop the UI that would fix it.
    if (!setup.SetupMode)
    {
        log.LogInformation("Vehicle: {Vehicle}.", host.Services.GetRequiredService<EvInfo>().Describe());
    }

    // Said explicitly because nothing else says it: Kestrel's own "Now listening on" is logged under
    // Microsoft.Hosting.Lifetime, which the Serilog configuration holds at Warning. Without this line a
    // log file cannot answer "was the UI up, and on which port" -- and the answer is a listening socket
    // on the LAN, which is exactly the sort of thing an operator should be able to audit after the fact.
    var web = host.Services.GetRequiredService<IOptions<WebOptions>>().Value;
    if (web.Enabled)
    {
        log.LogInformation(
            "Web UI enabled; listening on port {Port} (all interfaces, plain HTTP), login {LoginState}.",
            web.Port,
            web.AuthenticationRequired ? "required" : "not required");
    }

    // An unset zone means "ask the OS", which is right on Linux -- the container's TZ sets it. On
    // Windows it is a trap: .NET ignores TZ there, so the container runs in UTC and every session is
    // recorded against the wrong day, with nothing in the logs to say so. Say so.
    if (OperatingSystem.IsWindows()
        && string.IsNullOrWhiteSpace(host.Services.GetRequiredService<IOptions<ControllerOptions>>().Value.TimeZone))
    {
        log.LogWarning(
            "Controller:TimeZone is not set and this is Windows, where .NET ignores the TZ environment "
            + "variable. Local time is {Zone}. Set Controller__TimeZone to a Windows id (e.g. "
            + "\"Central Europe Standard Time\") or the day boundary and recorded sessions will be wrong.",
            TimeZoneInfo.Local.Id);
    }

    // Resolved before Run() rather than after it: Run() disposes the service provider on its way out, so
    // asking the container for anything once it returns throws ObjectDisposedException -- which, at this
    // point in the file, means the process aborts instead of exiting with the code below.
    var shutdown = host.Services.GetRequiredService<HostShutdown>();

    // The counterpart to the "starting" line above: a run that ends without its closing line ended badly.
    // Armed here, before Run(), because it hooks ApplicationStopped -- see LogWhenStopped().
    shutdown.LogWhenStopped();

    host.Run();

    // A restart where nothing outside would start the process again -- `dotnet run`, the debugger, the
    // Windows zip: build a fresh host in this one instead. Run() has disposed the old host by now, so the
    // port, the log file and the Modbus and MQTT clients are released, and the new host reads every
    // configuration source afresh -- appsettings, pv-system.json, the secret store -- which is all a
    // restart is for. Under Docker or systemd the process exits instead and the supervisor starts a new
    // one, which keeps those runs exactly what they were.
    if (shutdown.RestartRequested && shutdown.RestartsInProcess)
    {
        continue;
    }

    // How this run ended, said in the only language a container restart policy understands. 0 means an
    // operator pressed Stop and the service is meant to stay down; 143 means the platform terminated us
    // -- a reboot, a daemon restart, `docker compose restart` -- and the controller has to come back by
    // itself. .NET does not distinguish the two on its own, which is what this line is for.
    return shutdown.ExitCode;
}
