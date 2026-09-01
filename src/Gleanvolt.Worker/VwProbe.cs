using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Worker;

/// <summary>
/// The console harness issue #139 asks for: sign in, download, and print a <c>VehicleState</c> for
/// the reference car — without a hosted service, a broker or a dashboard anywhere near it.
///
/// <para>Two jobs beyond proving the client works. It prints <b>every field the mapper did not
/// recognise</b>, because the portal's vocabulary in <see cref="VwGroupFieldNames"/> was written from
/// a description rather than a capture and this is how the gap announces itself. And with
/// <c>--save-fixture</c> it writes a <b>sanitised</b> bundle, so the committed fixtures can stop being
/// synthetic — see <c>tests/Gleanvolt.Infrastructure.Tests/Fixtures/VwGroup/README.md</c>.</para>
///
/// <para>Offline, like <c>hash-password</c>: no configuration is built and no socket is opened, so it
/// cannot disturb a running controller.</para>
/// </summary>
internal static class VwProbe
{
    public const string Usage = """
        Usage: dotnet run --project src/Gleanvolt.Worker -- vw-probe [options]

          --client-id <id>       the brand's OIDC client id           (or VW_CLIENT_ID)
          --username <email>     the VW ID                            (or VW_USERNAME)
          --vin <vin>            which car, if the account sees several (or VW_VIN)
          --save-fixture <path>  write the sanitised bundle here, for the parser tests
          --portal <url>         override the portal base URL         (or VW_PORTAL_BASE)
          --identity <url>       override the identity provider       (or VW_IDENTITY_BASE)

        The password is read from VW_PASSWORD, or prompted for. It is never written anywhere.
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var flags = ReadFlags(args);

        if (flags.ContainsKey("help") || flags.ContainsKey("h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var options = new VwGroupPortalOptions
        {
            ClientId = Value(flags, "client-id", "VW_CLIENT_ID"),
            Username = Value(flags, "username", "VW_USERNAME"),
            Password = Environment.GetEnvironmentVariable("VW_PASSWORD") ?? Prompt(),
            Vin = Value(flags, "vin", "VW_VIN"),
            // Overridable so the whole chain can be exercised against a stub before it is pointed at
            // a real account. Against the portal itself neither of these is ever passed.
            PortalBaseUrl = Fallback(
                Value(flags, "portal", "VW_PORTAL_BASE"), new VwGroupPortalOptions().PortalBaseUrl),
            IdentityBaseUrl = Fallback(
                Value(flags, "identity", "VW_IDENTITY_BASE"), new VwGroupPortalOptions().IdentityBaseUrl),
        };

        if (!options.IsConfigured)
        {
            Console.Error.WriteLine($"Missing {options.DescribeWhatIsMissing()}.\n\n{Usage}");
            return 2;
        }

        using var handler = VwGroupSignIn.CreateHandler(new CookieContainer());
        using var http = new HttpClient(handler);

        var client = new VwGroupPortalClient(http, options);

        try
        {
            var vehicle = await client.GetVehicleAsync().ConfigureAwait(false);
            Console.WriteLine($"Vehicle:  {vehicle.MaskedVin}  (data request {vehicle.RequestId})");

            var url = await client.GetNewestDatasetUrlAsync(vehicle).ConfigureAwait(false);
            var archive = await client.DownloadAsync(url).ConfigureAwait(false);
            Console.WriteLine($"Download: {archive.Length:N0} bytes");

            if (!VwGroupReportBundle.TryRead(archive, out var snapshots, out var error))
            {
                Console.Error.WriteLine($"Unusable bundle: {error}");
                return 1;
            }

            Console.WriteLine($"Snapshots: {snapshots.Count}, "
                + $"{snapshots[0].CapturedAt:u} … {snapshots[^1].CapturedAt:u}");

            var result = VwGroupVehicleStateMapper.Map(snapshots, vehicle.MaskedVin);

            if (result.State is null)
            {
                // #73's rule made visible: a bundle that is present-but-unusable is rejected whole,
                // and the reason is the diagnosis.
                Console.Error.WriteLine($"Nothing usable in it: {result.Error}");
                return 1;
            }

            Print(result);

            if (flags.TryGetValue("save-fixture", out var path) && !string.IsNullOrWhiteSpace(path))
            {
                await SaveFixtureAsync(archive, path).ConfigureAwait(false);
                Console.WriteLine($"\nSanitised fixture written to {path}.");
            }

            return 0;
        }
        catch (VwGroupPortalException failure)
        {
            // The kinds are the point: one of these wants a browser and must never be retried.
            Console.Error.WriteLine($"\n{failure.Failure}: {failure.Message}");
            Console.Error.WriteLine(
                failure.IsWorthRetrying ? "Worth trying again later." : "Retrying will not help.");

            return 1;
        }
    }

    private static void Print(VwGroupMappingResult result)
    {
        var state = result.State!;

        Console.WriteLine($"""

            VehicleState
              CapturedAt           {state.CapturedAt:o}
              SocPercent           {Show(state.SocPercent)}
              RangeKm              {Show(state.RangeKm)}
              ChargeTimeRemaining  {(state.ChargeTimeRemaining is { } left ? $"{left.TotalMinutes:N0} min" : "—")}
              ChargeState          {state.ChargeState}
              PlugState            {state.PlugState}

            Also carried (nothing reads these yet)
              Target SOC           {Show(result.TargetSocPercent)}
              Odometer             {Show(result.OdometerKm)}
            """);

        if (result.UnmappedFields.Count == 0)
        {
            Console.WriteLine("\nEvery field in the bundle was recognised.");
            return;
        }

        // The most useful output this harness produces: what VwGroupFieldNames is missing, in one
        // glance rather than a week of wondering why the SOC is null.
        Console.WriteLine($"\n{result.UnmappedFields.Count} field(s) nothing here reads — if the SOC, "
            + "range, charge state or plug state above is missing, its real name is in this list:");

        foreach (var field in result.UnmappedFields)
        {
            Console.WriteLine($"  {field}");
        }
    }

    private static string Show(double? value) =>
        value is { } number ? number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "—";

    /// <summary>
    /// Writes the bundle's JSON out with anything identifying replaced.
    ///
    /// <para>A fixture is committed to a public repository, and this one comes off a real car: the
    /// VIN, the coordinates and the account identifiers all have to go, and the readings that make the
    /// fixture worth having stay. Conservative on purpose — a field whose <em>name</em> suggests
    /// identity is redacted whether or not its value looks like one.</para>
    /// </summary>
    private static async Task SaveFixtureAsync(byte[] archive, string path)
    {
        // Words that mean "this identifies the car or its owner". Deliberately not "name" or "id":
        // `dataFieldName` and `key` ARE the vocabulary this capture exists to record, and redacting
        // them would leave a fixture that proves nothing.
        var identifying = new[]
        {
            "vin", "latitude", "longitude", "position", "location", "address", "mail", "phone",
            "userid", "customer", "account", "token", "licenseplate", "registration",
        };

        using var stream = new MemoryStream(archive, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var output = new StringBuilder();

        foreach (var entry in zip.Entries.Where(entry =>
                     entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var content = entry.Open();
            using var document = await JsonDocument.ParseAsync(content).ConfigureAwait(false);

            using var buffer = new MemoryStream();

            // Relaxed escaping so the file reads as the portal wrote it -- a fixture nobody can read
            // is a fixture nobody checks. It is written to disk, never to a page.
            var writerOptions = new JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            using (var writer = new Utf8JsonWriter(buffer, writerOptions))
            {
                Redact(document.RootElement, writer, identifying, propertyName: null);
            }

            output.AppendLine($"// {entry.FullName}");
            output.AppendLine(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        await File.WriteAllTextAsync(path, output.ToString()).ConfigureAwait(false);
    }

    private static void Redact(
        JsonElement element, Utf8JsonWriter writer, string[] identifying, string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                // A reading names itself in a sibling property, so the whole entry is judged by the
                // dataFieldName it carries rather than by the property its value happens to sit
                // under -- which would otherwise redact every `value` or none of them.
                var readingName = ReadingName(element);

                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Redact(
                        property.Value, writer, identifying,
                        readingName is not null && IsValueProperty(property.Name)
                            ? readingName
                            : property.Name);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray())
                {
                    Redact(item, writer, identifying, propertyName);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                var name = propertyName ?? string.Empty;

                // A reading's name lives in a sibling property, so a Data entry is judged by the
                // dataFieldName it carries rather than by the property the value sits under.
                writer.WriteStringValue(
                    identifying.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))
                        ? "REDACTED"
                        : text);
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    // The name a Data entry gives itself, when this object is one.
    private static string? ReadingName(JsonElement element)
    {
        foreach (var candidate in VwGroupReportBundle.FieldNameProperties)
        {
            if (element.TryGetProperty(candidate, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static bool IsValueProperty(string name) =>
        VwGroupReportBundle.ValueProperties.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string Prompt()
    {
        Console.Error.Write("VW ID password: ");
        var password = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return password.ToString();
            }

            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }
    }

    private static Dictionary<string, string> ReadFlags(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)
                && !args[index].StartsWith('-'))
            {
                continue;
            }

            var name = args[index].TrimStart('-');
            var value = index + 1 < args.Length && !args[index + 1].StartsWith('-')
                ? args[++index]
                : string.Empty;

            flags[name] = value;
        }

        return flags;
    }

    private static string Value(Dictionary<string, string> flags, string flag, string variable) =>
        flags.TryGetValue(flag, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : Environment.GetEnvironmentVariable(variable) ?? string.Empty;

    private static string Fallback(string value, string whenEmpty) =>
        string.IsNullOrWhiteSpace(value) ? whenEmpty : value;
}
