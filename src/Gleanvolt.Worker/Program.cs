using Microsoft.Extensions.Options;
using Serilog;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Web;
using Gleanvolt.Web.Auth;
using Gleanvolt.Worker;

// A tiny offline tool rather than a whole second entry point: the password is a secret that must
// never live in appsettings.json, and this is the only way to produce the hash that belongs in
// Web__PasswordHash instead. Takes precedence over everything below -- no configuration, no
// listening socket, just the hash on stdout.
if (args is ["hash-password", var plainPassword])
{
    Console.WriteLine(WebPasswordHasher.Hash(plainPassword));
    return 0;
}

// Load secrets (e.g. Solcast__ApiKey) from an untracked .env file into the process environment
// before configuration is built, so they reach the app whether it's started via `dotnet run` or
// the VS Code debugger -- without living in any committed file. Real env vars still take priority.
DotEnv.Load(Directory.GetCurrentDirectory());

// Serilog swallows failures inside its own sinks. That silence is dangerous in the container: if the
// bind-mounted logs directory isn't writable by the image's non-root user, the console keeps logging
// normally and the log files simply never appear -- verified, and invisible without this line.
Serilog.Debugging.SelfLog.Enable(Console.Error);

// A web host, but only sometimes a web server. The self-hosted UI (issue #44) lives in Gleanvolt.Web and
// is hosted here, so the process needs ASP.NET's builder; when the UI is switched off the process
// must still be exactly what it was before — a headless worker that listens on nothing. See
// GleanvoltHostingExtensions for how that is enforced rather than merely intended.
var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Services.AddSerilog(config => config
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext());

// Everything the controller is: polling, the control strategies, the session store, the Home
// Assistant integration and the web UI. It lives in Gleanvolt.Hosting so that this file is a host and
// nothing more -- see that assembly for why each service is registered the way it is.
builder.AddGleanvolt();

var host = builder.Build();

host.UseGleanvolt();

var log = host.Services.GetRequiredService<ILogger<Program>>();

// First line in the log, before anything can go wrong: a log file or a `docker logs` dump is
// otherwise untraceable to the build that produced it. A "-dev" suffix with no commit means a
// local build rather than anything CI published.
log.LogInformation("Gleanvolt {Version} starting.", BuildInfo.Describe());

// What this process thinks it is: which installation, and which boxes it will be talking to (issue
// #111). A log file that cannot answer that is a log file nobody can read six months later.
log.LogInformation("PV system: {System}.", host.Services.GetRequiredService<PvSystemInfo>().Describe());

// And the car, on the same terms (#124): "which installation was this, and what car did it think it
// had" should both be answerable from a `docker logs` dump with no configuration file beside it.
log.LogInformation("Vehicle: {Vehicle}.", host.Services.GetRequiredService<EvInfo>().Describe());

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

// How this run ended, said in the only language a container restart policy understands. 0 means an
// operator pressed Stop and the service is meant to stay down; 143 means the platform terminated us
// -- a reboot, a daemon restart, `docker compose restart` -- and the controller has to come back by
// itself. .NET does not distinguish the two on its own, which is what this line is for.
return shutdown.ExitCode;
