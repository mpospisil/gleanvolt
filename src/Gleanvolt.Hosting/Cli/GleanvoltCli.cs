using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Models;
using Gleanvolt.Discovery;
using Gleanvolt.Discovery.Cli;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Setup;
using Gleanvolt.Infrastructure.Secrets;
using Gleanvolt.Web.Auth;

namespace Gleanvolt.Hosting.Cli;

/// <summary>
/// The command-line route to a configured Gleanvolt (#303), beside the web wizard rather than instead
/// of it. Every command saves through <see cref="SetupDocument"/> -- the same allowlist, the same
/// checks, the same files as the wizard and Settings -- and none of them starts the host.
///
/// <para><b>Safeguards.</b> Under the .deb, refused unless run as the service user: a file it writes
/// as anyone else is one the service may not be able to read. On Windows with DPAPI, a warning that
/// the secrets are sealed to this user. A setup file holding secrets is refused if every user can read
/// it, and when it is applied, the reminder that it still holds them. The overrides file is written
/// under the same cross-process lock the web UI takes.</para>
/// </summary>
public static class GleanvoltCli
{
    public const string Usage = """
        Gleanvolt command-line setup. Each command saves for the next start; restart Gleanvolt to apply.

          setup                      Ask for each setting in turn, as the web wizard does.
          setup --file <site.json>   Check a setup file and apply it. Lists every problem; exit 1 if any.
          setup --validate <file>    Check a setup file only.
          setup --print-schema       Print the JSON schema of what a setup file may hold.
          set-password               Set a new web UI password and print its recovery key.
          reset-password             The same: being able to run this as the service user is the proof.
          discover [options]         Search the network for the inverter and charger (--help for options).

        Under the .deb, run as the service user:  sudo -u gleanvolt /opt/gleanvolt/Gleanvolt.Worker setup …
        In Docker:  docker compose run --rm gleanvolt-controller setup --file /app/data/site.json
        """;

    private static readonly string[] Commands = ["setup", "set-password", "reset-password", "discover"];

    /// <summary>Whether <paramref name="args"/> is one of these commands rather than a start of the service.</summary>
    public static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && Commands.Contains(args[0], StringComparer.Ordinal);

    public static async Task<int> RunAsync(string[] args, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;

        switch (args)
        {
            case ["discover", .. var rest]:
                return await DiscoverCommand.RunAsync(rest, "gleanvolt discover", console.Out, console.Error, cancellationToken)
                    .ConfigureAwait(false);

            case ["setup", "--print-schema"]:
                await console.Out.WriteLineAsync(Schema()).ConfigureAwait(false);
                return 0;

            case ["setup", "--validate", var file]:
                return Validate(file, context);

            case ["setup", "--file", var file]:
                return ApplyFile(file, context);

            case ["setup"]:
                return await InteractiveAsync(context, cancellationToken).ConfigureAwait(false);

            case ["set-password" or "reset-password"]:
                return SetPassword(context);

            default:
                await console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
                return args is [_, "--help" or "-h"] ? 0 : 2;
        }
    }

    // -- setup --validate / --file

    private static int Validate(string path, CliContext context)
    {
        if (Load(path, context) is not { } document)
        {
            return 1;
        }

        if (HasSecrets(document) && context.Platform.WorldReadable(path))
        {
            context.Console.Error.WriteLine(
                $"Warning: {path} holds secrets and every user on this machine can read it. "
                + "`setup --file` refuses it until it is private (chmod 600).");
        }

        var problems = document.Validate(context.Configuration);
        if (problems.Count > 0)
        {
            WriteProblems(context, problems);
            return 1;
        }

        context.Console.Out.WriteLine($"{path} is valid: {document.Values.Count} setting(s) would be applied.");
        return 0;
    }

    private static int ApplyFile(string path, CliContext context)
    {
        if (!RightUser(context) || Load(path, context) is not { } document)
        {
            return 1;
        }

        var secrets = HasSecrets(document);
        if (secrets && context.Platform.WorldReadable(path))
        {
            context.Console.Error.WriteLine(
                $"{path} holds secrets and every user on this machine can read it. Make it private first "
                + $"(chmod 600 {path}), or take the secrets out of it.");
            return 1;
        }

        var exit = Apply(document, context);

        if (exit == 0 && secrets)
        {
            context.Console.Error.WriteLine(
                $"Note: the secrets are now in the secret store, and {path} still holds them. Delete it, or keep it private.");
        }

        return exit;
    }

    private static SetupDocument? Load(string path, CliContext context)
    {
        try
        {
            return SetupDocument.FromJson(File.ReadAllText(path));
        }
        catch (FormatException ex)
        {
            context.Console.Error.WriteLine($"{path}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Console.Error.WriteLine($"{path} could not be read: {ex.Message}");
        }

        return null;
    }

    private static bool HasSecrets(SetupDocument document) =>
        document.Values.Any(entry => SetupDocument.IsSecret(entry.Key) && !string.IsNullOrEmpty(entry.Value));

    // The secrets that go to the secret store: all but the web password, which is saved as a hash.
    private static bool HasStoredSecrets(SetupDocument document) =>
        document.Values.Any(entry => SetupDocument.IsSecret(entry.Key) && !string.IsNullOrEmpty(entry.Value)
            && !entry.Key.Equals(SetupDocument.WebPassword, StringComparison.OrdinalIgnoreCase));

    // The one way anything here is saved: the document applied as the wizard applies it.
    private static int Apply(SetupDocument document, CliContext context)
    {
        if (PvSystemOverrides.Find(context.Configuration) is not { } overrides)
        {
            context.Console.Error.WriteLine("This configuration has no overrides file to save to.");
            return 1;
        }

        var password = PasswordService(context);
        var result = document.Apply(new SetupTarget(context.Configuration, overrides.Path, context.Secrets, password));

        if (!result.Applied)
        {
            WriteProblems(context, result.Problems);
            return 1;
        }

        var output = context.Console.Out;
        output.WriteLine($"Saved to {overrides.Path}{(HasStoredSecrets(document) ? " and the secret store" : "")}.");

        if (HasStoredSecrets(document))
        {
            WarnSealedToThisUser(context);
        }

        if (result.RecoveryKey is { } recoveryKey)
        {
            WriteRecoveryKey(output, recoveryKey);
        }

        WriteNextSteps(context);
        return 0;
    }

    // -- set-password / reset-password

    private static int SetPassword(CliContext context)
    {
        if (!RightUser(context))
        {
            return 1;
        }

        if (PvSystemOverrides.Find(context.Configuration) is null)
        {
            context.Console.Error.WriteLine("This configuration has no overrides file to save the password to.");
            return 1;
        }

        if (AskNewPassword(context) is not { } password)
        {
            return 1;
        }

        var result = PasswordService(context).ResetFromHost(password);

        if (!result.Succeeded)
        {
            context.Console.Error.WriteLine(result.Error switch
            {
                PasswordError.TooShort => $"The password must be at least {WebPasswordService.MinimumLength} characters.",
                _ => "The password could not be saved. Check that the data directory is writable by this user.",
            });
            return 1;
        }

        context.Console.Out.WriteLine("Password saved. Every signed-in browser is signed out when Gleanvolt restarts.");
        WriteRecoveryKey(context.Console.Out, result.RecoveryKey!);
        WriteNextSteps(context);
        return 0;
    }

    private static string? AskNewPassword(CliContext context)
    {
        var console = context.Console;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            console.Out.Write($"New web UI password (at least {WebPasswordService.MinimumLength} characters): ");
            var first = console.ReadHidden();
            if (first is null)
            {
                console.Error.WriteLine("No password given.");
                return null;
            }

            if (first.Length < WebPasswordService.MinimumLength)
            {
                console.Error.WriteLine($"Too short: at least {WebPasswordService.MinimumLength} characters.");
                continue;
            }

            console.Out.Write("The same again: ");
            if (console.ReadHidden() is not { } second)
            {
                console.Error.WriteLine("No password given.");
                return null;
            }

            if (second == first)
            {
                return first;
            }

            console.Error.WriteLine("The two do not match.");
        }

        console.Error.WriteLine("No password set.");
        return null;
    }

    // -- setup, interactively

    private static async Task<int> InteractiveAsync(CliContext context, CancellationToken cancellationToken)
    {
        if (!RightUser(context))
        {
            return 1;
        }

        var console = context.Console;
        var configuration = context.Configuration;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        console.Out.WriteLine("Gleanvolt setup. Press Enter to keep the value in [brackets]; Ctrl+C stops without saving.");
        console.Out.WriteLine();

        try
        {
            // 1. The password, when there is none.
            var passwordSet = !string.IsNullOrWhiteSpace(configuration[WebPasswordStore.Key])
                || configuration.GetValue<bool?>("Web:RequireAuthentication") == false;

            if (!passwordSet)
            {
                console.Out.WriteLine("Web UI password");
                values[SetupDocument.WebPassword] = AskNewPassword(context) ?? throw new EndOfInput();
                console.Out.WriteLine();
            }

            // 2. The devices, with the same search the wizard offers.
            console.Out.WriteLine("Devices");
            var found = new List<DiscoveredDevice>();

            if (Confirm(console, "Search the network for the inverter and charger? It takes a minute or two.", true))
            {
                console.Out.WriteLine("Searching…");
                found = [.. (await context.Platform.Discover(cancellationToken).ConfigureAwait(false))
                    .Where(device => device.UnitId is not null)];

                if (found.Count == 0)
                {
                    console.Out.WriteLine("Nothing answered. Is Modbus TCP enabled in the inverter's own menu? Type the addresses instead.");
                }

                foreach (var device in found)
                {
                    console.Out.WriteLine(
                        $"  {device.Host}:{device.Port} unit {device.UnitId} — {device.Model ?? "unidentified"}"
                        + (device.Role is { } role ? $" ({role.ToString().ToLowerInvariant()})" : ""));
                }
            }

            ChooseDevice(console, configuration, values, found, PvDeviceRole.Inverter,
                PvSystemSettingKeys.InverterHost, PvSystemSettingKeys.InverterPort, PvSystemSettingKeys.InverterUnitId);
            ChooseDevice(console, configuration, values, found, PvDeviceRole.Charger,
                PvSystemSettingKeys.ChargerHost, PvSystemSettingKeys.ChargerPort, PvSystemSettingKeys.ChargerUnitId);
            console.Out.WriteLine();

            // 3. The site.
            console.Out.WriteLine("Site");
            Ask(console, configuration, values, PvSystemSettingKeys.Latitude, "Latitude, in decimal degrees (e.g. 50.0755)");
            Ask(console, configuration, values, PvSystemSettingKeys.Longitude, "Longitude, in decimal degrees (e.g. 14.4378)");
            Ask(console, configuration, values, PvSystemSettingKeys.TimeZone, "Time zone (e.g. Europe/Prague)",
                fallback: OperatingSystem.IsWindows() ? null : TimeZoneInfo.Local.Id is not ("UTC" or "Etc/UTC") ? TimeZoneInfo.Local.Id : null);
            Ask(console, configuration, values, PvSystemSettingKeys.HomeBatteryCapacityKWh, "Home battery usable capacity, kWh (optional)");
            console.Out.WriteLine();

            // 4. What can be skipped.
            console.Out.WriteLine("Optional — press Enter to skip");
            AskSecret(console, values, SetupDocument.SolcastApiKey, "Solcast API key");
            if (values.ContainsKey(SetupDocument.SolcastApiKey))
            {
                Ask(console, configuration, values, SetupDocument.SolcastResourceId, "Solcast rooftop site resource id");
            }

            AskSecret(console, values, SetupDocument.WeatherApiKey, "OpenWeatherMap API key");
            console.Out.WriteLine("The car, the tariff, Home Assistant and the API are set on Settings, or with setup --file.");
            console.Out.WriteLine();
        }
        catch (EndOfInput)
        {
            console.Error.WriteLine("Input ended; nothing was saved.");
            return 1;
        }

        if (values.Count == 0)
        {
            console.Out.WriteLine("Nothing changed.");
            return 0;
        }

        var document = SetupDocument.From(values);
        var problems = document.Validate(configuration);
        if (problems.Count > 0)
        {
            WriteProblems(context, problems);
            return 1;
        }

        if (!Confirm(console, $"Save {values.Count} setting(s)?", true))
        {
            console.Out.WriteLine("Nothing was saved.");
            return 0;
        }

        return Apply(document, context);
    }

    // A found device of the role, or the current address, offered as the default; the port and unit id
    // follow the address chosen when the search found it.
    private static void ChooseDevice(
        CliConsole console,
        IConfiguration configuration,
        Dictionary<string, string> values,
        IReadOnlyList<DiscoveredDevice> found,
        PvDeviceRole role,
        string hostKey,
        string portKey,
        string unitKey)
    {
        var suggested = found.FirstOrDefault(device => device.Role == role)?.Host;
        var label = role == PvDeviceRole.Inverter ? "Inverter address" : "Charger address";

        var host = Ask(console, configuration, values, hostKey, label, fallback: suggested);

        if (host is not null && found.FirstOrDefault(device => device.Host == host) is { } device)
        {
            values[portKey] = device.Port.ToString(CultureInfo.InvariantCulture);
            values[unitKey] = (device.UnitId ?? 1).ToString(CultureInfo.InvariantCulture);

            if (device.Role is { } identified && identified != role)
            {
                console.Out.WriteLine(
                    $"  Note: {host} identified itself as the {identified.ToString().ToLowerInvariant()}. "
                    + "A swapped pair answers like the right one and shows only once running.");
            }
        }
    }

    // One prompt. The current value, or the fallback, is the default; an answer the same as the
    // current value is not recorded, so the file lists only real changes.
    private static string? Ask(
        CliConsole console,
        IConfiguration configuration,
        Dictionary<string, string> values,
        string key,
        string label,
        string? fallback = null)
    {
        var current = configuration[key];
        var shown = string.IsNullOrWhiteSpace(current) ? fallback : current;

        console.Out.Write(shown is null ? $"{label}: " : $"{label} [{shown}]: ");
        var answer = console.In.ReadLine() ?? throw new EndOfInput();
        answer = answer.Trim();

        var value = answer.Length > 0 ? answer : shown;
        if (value is not null && value != current)
        {
            values[key] = value;
        }

        return value;
    }

    private static void AskSecret(CliConsole console, Dictionary<string, string> values, string key, string label)
    {
        console.Out.Write($"{label}: ");
        var answer = console.ReadHidden() ?? throw new EndOfInput();

        if (answer.Trim().Length > 0)
        {
            values[key] = answer.Trim();
        }
    }

    private static bool Confirm(CliConsole console, string question, bool yes)
    {
        console.Out.Write($"{question} [{(yes ? "Y/n" : "y/N")}] ");
        var answer = (console.In.ReadLine() ?? throw new EndOfInput()).Trim();

        return answer.Length == 0 ? yes : answer.StartsWith('y') || answer.StartsWith('Y');
    }

    private sealed class EndOfInput : Exception;

    // -- what every command shares

    private static bool RightUser(CliContext context)
    {
        if (context.Platform.ServiceUser is { } service
            && !string.Equals(context.Platform.UserName, service, StringComparison.Ordinal))
        {
            context.Console.Error.WriteLine(
                $"Run this as the service user, {service}: the service could not read what {context.Platform.UserName} "
                + $"would write. For example:{Environment.NewLine}"
                + $"  sudo -u {service} /opt/gleanvolt/Gleanvolt.Worker setup …");
            return false;
        }

        return true;
    }

    private static void WarnSealedToThisUser(CliContext context)
    {
        if (context.Platform.IsWindows && context.Platform.SecretStore == SecretStoreKind.Dpapi)
        {
            context.Console.Error.WriteLine(
                $"Note: the secrets are sealed to the Windows user {context.Platform.UserName} (DPAPI). Gleanvolt "
                + "can read them only when it runs as this user too.");
        }
    }

    private static WebPasswordService PasswordService(CliContext context) => new(
        new WebPasswordState(context.Configuration[WebPasswordStore.Key], context.Configuration[WebPasswordStore.RecoveryKeyHashKey]),
        new WebPasswordStore(context.Configuration),
        new PasswordResetOptions(Path.Combine(context.ContentRoot, "password-reset-code.txt")),
        NullLogger<WebPasswordService>.Instance);

    private static void WriteProblems(CliContext context, IReadOnlyList<string> problems)
    {
        context.Console.Error.WriteLine(problems.Count == 1 ? "Not saved:" : $"Not saved — {problems.Count} problems:");
        foreach (var problem in problems)
        {
            context.Console.Error.WriteLine($"  - {problem}");
        }
    }

    private static void WriteRecoveryKey(TextWriter output, string recoveryKey)
    {
        output.WriteLine();
        output.WriteLine($"Recovery key: {recoveryKey}");
        output.WriteLine("Keep it somewhere safe: it resets the password from the login page. It is not shown again.");
        output.WriteLine();
    }

    private static void WriteNextSteps(CliContext context) =>
        context.Console.Out.WriteLine(
            context.Platform.ServiceUser is not null
                ? "Restart Gleanvolt to apply it: sudo systemctl restart gleanvolt"
                : "Restart Gleanvolt to apply it (in Docker: docker compose restart gleanvolt-controller).");

    // -- setup --print-schema

    /// <summary>
    /// The JSON schema of a setup file: every key <see cref="SetupDocument"/> accepts, in the shape of
    /// <c>appsettings.json</c>. Values may be strings, numbers or booleans, as configuration reads them
    /// all as text; the secrets say so.
    /// </summary>
    public static string Schema()
    {
        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Gleanvolt setup file",
            ["description"] =
                "Applied with `gleanvolt setup --file`. Secrets are moved to the secret store; everything else "
                + "is saved to the overrides file. An empty value removes a saved one.",
        };

        foreach (var (name, value) in Node())
        {
            root[name] = value?.DeepClone();
        }

        foreach (var key in SetupDocument.AllowedKeys)
        {
            var leaf = new JsonObject { ["type"] = new JsonArray("string", "number", "boolean") };
            if (key == SetupDocument.WebPassword)
            {
                leaf["writeOnly"] = true;
                leaf["description"] = "The web UI password, at least 8 characters. Saved only as a hash; prints a recovery key.";
            }
            else if (SetupDocument.IsSecret(key))
            {
                leaf["writeOnly"] = true;
                leaf["description"] = "Secret: moved to the secret store, never to the overrides file.";
            }

            Place(root, key.Split(':'), leaf);
        }

        var api = Child(Child(root, "Api"), "Keys");
        api["description"] = "API keys by client name, e.g. { \"claude\": \"<key>\" }. Secrets.";
        api["additionalProperties"] = new JsonObject { ["type"] = "string", ["writeOnly"] = true };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Node() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false,
    };

    private static JsonObject Child(JsonObject parent, string name)
    {
        var properties = (JsonObject)parent["properties"]!;
        if (properties[name] is not JsonObject child)
        {
            child = Node();
            properties[name] = child;
        }

        return child;
    }

    // Pv:Chargers:0:Host -- a numeric segment is the first item of an array, as appsettings.json writes it.
    private static void Place(JsonObject node, string[] segments, JsonObject leaf)
    {
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (int.TryParse(segments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                var properties = (JsonObject)node["properties"]!;
                if (properties[segments[index]] is not JsonObject array)
                {
                    array = new JsonObject { ["type"] = "array", ["maxItems"] = 1, ["items"] = Node() };
                    properties[segments[index]] = array;
                }

                node = (JsonObject)array["items"]!;
                index++;
                continue;
            }

            node = Child(node, segments[index]);
        }

        ((JsonObject)node["properties"]!)[segments[^1]] = leaf;
    }
}
