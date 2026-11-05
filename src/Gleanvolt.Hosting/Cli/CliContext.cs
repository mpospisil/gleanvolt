using Gleanvolt.Core.Interfaces;
using Gleanvolt.Discovery;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Infrastructure.Secrets;

namespace Gleanvolt.Hosting.Cli;

/// <summary>
/// What a command-line run works against (#303): the configuration the service itself would read,
/// where it is, the console, and the facts about this machine the safeguards need. One record, so a
/// test hands in a temporary directory, a scripted console and a made-up platform.
/// </summary>
public sealed record CliContext(
    IConfiguration Configuration,
    string ContentRoot,
    CliConsole Console,
    CliPlatform Platform,
    ISecretStore Secrets)
{
    /// <summary>
    /// The configuration this host's service reads, built the way the host builds it -- appsettings,
    /// the environment, the overrides file, the secret store -- without building the host.
    /// </summary>
    public static CliContext ForThisHost(Func<IConfiguration, CliPlatform> platform)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        var contentRoot = builder.Environment.ContentRootPath;

        builder.Configuration.AddPvSystemOverrides(contentRoot);
        builder.Configuration.AddVehicleAccountPassword(contentRoot);
        builder.Configuration.AddIntegrationSecrets(contentRoot);

        return new CliContext(
            builder.Configuration,
            contentRoot,
            CliConsole.System(),
            platform(builder.Configuration),
            VehicleAccountSecret.Open(builder.Configuration, contentRoot));
    }
}

/// <summary>
/// The console, with hidden input for passwords and keys. When input is redirected -- a script, or a
/// test -- a hidden read is a plain line read: there is nothing on screen to hide.
/// </summary>
public sealed class CliConsole(TextReader input, TextWriter output, TextWriter error, Func<string?>? readHidden = null)
{
    public TextReader In { get; } = input;

    public TextWriter Out { get; } = output;

    public TextWriter Error { get; } = error;

    /// <summary>The real console.</summary>
    public static CliConsole System() => new(
        global::System.Console.In,
        global::System.Console.Out,
        global::System.Console.Error,
        global::System.Console.IsInputRedirected ? null : ReadMasked);

    /// <summary>A line typed without echo, or null at the end of the input.</summary>
    public string? ReadHidden() => readHidden is null ? In.ReadLine() : readHidden();

    private static string? ReadMasked()
    {
        var typed = new global::System.Text.StringBuilder();

        while (true)
        {
            var key = global::System.Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                global::System.Console.WriteLine();
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
    }
}

/// <summary>
/// The facts about this machine the safeguards turn on. <see cref="Current"/> is the real one; tests
/// make their own.
/// </summary>
/// <param name="ServiceUser">The user the installed service runs as, when this is a .deb install; null otherwise.</param>
/// <param name="UserName">Who is running this.</param>
/// <param name="IsWindows">Whether secrets may be sealed with DPAPI to the user running this.</param>
/// <param name="SecretStore">Which secret store the configuration selects.</param>
/// <param name="WorldReadable">Whether a file can be read by every user on this machine.</param>
/// <param name="Discover">The device search the interactive setup offers.</param>
public sealed record CliPlatform(
    string? ServiceUser,
    string UserName,
    bool IsWindows,
    SecretStoreKind SecretStore,
    Func<string, bool> WorldReadable,
    Func<CancellationToken, Task<IReadOnlyList<DiscoveredDevice>>> Discover)
{
    public static CliPlatform Current(string? serviceUser, IConfiguration configuration)
    {
        var secrets = configuration.GetSection(SecretsOptions.SectionName).Get<SecretsOptions>() ?? new SecretsOptions();
        var store = SecretStoreSelection.Choose(secrets.Store, OperatingSystem.IsWindows(), SecretStoreSelection.RunningInContainer());

        return new CliPlatform(
            serviceUser,
            Environment.UserName,
            OperatingSystem.IsWindows(),
            store.Kind,
            path => !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & UnixFileMode.OtherRead) != 0,
            async cancellationToken =>
            {
                var selection = NetworkSelection.FromInterfaces(NetworkSelection.LocalInterfaces(), NetworkSelection.RunningInContainer());
                var report = await new DeviceScanner().ScanAsync(
                    ScanPlan.ForSubnets(selection.Subnets, selection.Warnings), _ => { }, cancellationToken).ConfigureAwait(false);
                return report.Devices;
            });
    }
}
