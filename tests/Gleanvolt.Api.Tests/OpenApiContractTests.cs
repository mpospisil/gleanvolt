using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gleanvolt.Api.Tests;

/// <summary>
/// The document is the deliverable: a generated client — an MCP tool surface most of all — is built
/// from it and nothing else, so a change to it is a change to somebody's integration.
///
/// <para>This locks the <b>shape</b> rather than the bytes: every operation id, every parameter, every
/// response code, and every schema property with its type and nullability, projected into a canonical
/// file that is checked in. A deliberate contract change shows up as a reviewable diff in
/// <c>OpenApiContract.json</c>; an accidental one — a renamed DTO property, a dropped nullable, an enum
/// member reordered into a different name — fails here. Formatting or ordering changes from a new SDK
/// do not, which is what keeps the check honest rather than merely noisy.</para>
///
/// <para>Regenerate after an intended change with
/// <c>GLEANVOLT_UPDATE_OPENAPI_CONTRACT=1 dotnet test tests/Gleanvolt.Api.Tests</c>, and read the
/// diff.</para>
/// </summary>
public sealed class OpenApiContractTests : IAsyncDisposable
{
    private readonly ApiTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static string SnapshotPath =>
        Path.Combine(AppContext.BaseDirectory, "OpenApiContract.json");

    private static string SourcePath
    {
        get
        {
            // Written back to the project rather than to the build output, which is what makes
            // regeneration produce a diff instead of a file nobody sees.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Gleanvolt.Api.Tests.csproj")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory?.FullName ?? AppContext.BaseDirectory, "OpenApiContract.json");
        }
    }

    [Fact]
    public async Task The_document_matches_the_checked_in_contract()
    {
        var client = await _host.StartAsync();

        var document = await (await client.GetAsync(GleanvoltApi.DocumentPath)).ReadAsync();
        var projected = Project(document).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        if (Environment.GetEnvironmentVariable("GLEANVOLT_UPDATE_OPENAPI_CONTRACT") == "1")
        {
            await File.WriteAllTextAsync(SourcePath, projected + Environment.NewLine);
        }

        var expected = (await File.ReadAllTextAsync(SnapshotPath)).Trim().ReplaceLineEndings("\n");

        Assert.Equal(expected, projected.Trim().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task The_document_declares_the_key_every_operation_needs()
    {
        var client = await _host.StartAsync();

        var document = await (await client.GetAsync(GleanvoltApi.DocumentPath)).ReadAsync();

        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("apiKey");
        Assert.Equal("http", scheme.Text("type"));
        Assert.Equal("bearer", scheme.Text("scheme"));

        // Declared on the document rather than per operation: every route here is behind the key, and a
        // client generated from this should send it without being told twice.
        Assert.True(document.GetProperty("security")[0].TryGetProperty("apiKey", out _));
    }

    [Fact]
    public async Task The_document_describes_only_the_api()
    {
        var client = await _host.StartAsync();

        var document = await (await client.GetAsync(GleanvoltApi.DocumentPath)).ReadAsync();

        Assert.All(
            document.GetProperty("paths").EnumerateObject(),
            path => Assert.StartsWith("/api/v1", path.Name));
    }

    [Fact]
    public async Task Every_operation_carries_a_description_for_whatever_reads_it()
    {
        var client = await _host.StartAsync();

        var document = await (await client.GetAsync(GleanvoltApi.DocumentPath)).ReadAsync();

        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                // A model choosing between tools reads these, so an operation without one is a tool
                // nobody can use correctly.
                Assert.False(string.IsNullOrWhiteSpace(operation.Value.Text("summary")));
                Assert.False(string.IsNullOrWhiteSpace(operation.Value.Text("description")));
                Assert.False(string.IsNullOrWhiteSpace(operation.Value.Text("operationId")));
            }
        }
    }

    [Fact]
    public async Task The_schemas_carry_the_reasoning_the_code_carries()
    {
        var client = await _host.StartAsync();

        var document = await (await client.GetAsync(GleanvoltApi.DocumentPath)).ReadAsync();
        var schemas = document.GetProperty("components").GetProperty("schemas");

        // The XML comments are the descriptions: what is written next to the property in C# is what a
        // client generator, and whatever reads its tool definitions, has to go on.
        var coverage = schemas.GetProperty("EnergyIntervalResponse").GetProperty("properties").GetProperty("coverage");
        Assert.Contains("Read this before trusting a row", coverage.Text("description"));

        var hold = schemas.GetProperty("BatteryHoldResponse").GetProperty("properties").GetProperty("active");
        Assert.Contains("the command register cannot be read", hold.Text("description"));
    }

    /// <summary>The contract, with everything that is presentation rather than contract left out.</summary>
    private static JsonObject Project(JsonElement document)
    {
        var paths = new JsonObject();

        foreach (var path in document.GetProperty("paths").EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var operations = new JsonObject();

            foreach (var operation in path.Value.EnumerateObject().OrderBy(o => o.Name, StringComparer.Ordinal))
            {
                var parameters = new JsonArray();
                if (operation.Value.TryGetProperty("parameters", out var declared))
                {
                    foreach (var parameter in declared.EnumerateArray()
                        .OrderBy(p => p.GetProperty("name").GetString(), StringComparer.Ordinal))
                    {
                        var required = parameter.TryGetProperty("required", out var flag) && flag.GetBoolean();
                        parameters.Add(
                            $"{parameter.Text("in")}:{parameter.Text("name")}"
                            + $"{(required ? " (required)" : "")} {Describe(parameter.GetProperty("schema"))}");
                    }
                }

                var responses = new JsonArray();
                foreach (var response in operation.Value.GetProperty("responses").EnumerateObject()
                    .OrderBy(r => r.Name, StringComparer.Ordinal))
                {
                    responses.Add($"{response.Name} {Body(response.Value)}");
                }

                operations[operation.Name] = new JsonObject
                {
                    ["operationId"] = operation.Value.Text("operationId"),
                    ["parameters"] = parameters,
                    ["requestBody"] = operation.Value.TryGetProperty("requestBody", out var body)
                        ? Body(body)
                        : null,
                    ["responses"] = responses,
                };
            }

            paths[path.Name] = operations;
        }

        var schemas = new JsonObject();
        if (document.GetProperty("components").TryGetProperty("schemas", out var declaredSchemas))
        {
            foreach (var schema in declaredSchemas.EnumerateObject().OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                schemas[schema.Name] = Detail(schema.Value);
            }
        }

        return new JsonObject
        {
            ["title"] = document.GetProperty("info").Text("title"),
            ["version"] = document.GetProperty("info").Text("version"),
            ["paths"] = paths,
            ["schemas"] = schemas,
        };
    }

    /// <summary>An object's properties, or the terse description of anything that is not one.</summary>
    private static JsonNode Detail(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties))
        {
            return Describe(schema);
        }

        var required = schema.TryGetProperty("required", out var declared)
            ? declared.EnumerateArray().Select(r => r.GetString()).ToHashSet(StringComparer.Ordinal)
            : [];

        var shape = new JsonObject();
        foreach (var property in properties.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            shape[property.Name] = Describe(property.Value) + (required.Contains(property.Name) ? " (required)" : "");
        }

        return shape;
    }

    /// <summary>One schema as a single readable token, so a diff reads as a sentence about the change.</summary>
    private static string Describe(JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            return "$" + reference.GetString()!.Split('/')[^1];
        }

        if (schema.TryGetProperty("enum", out var values))
        {
            return "enum[" + string.Join("|", values.EnumerateArray().Select(v => v.ToString())) + "]";
        }

        var nullable = false;
        var type = "any";

        if (schema.TryGetProperty("type", out var declared))
        {
            if (declared.ValueKind == JsonValueKind.Array)
            {
                var names = declared.EnumerateArray().Select(t => t.GetString()!).ToList();
                nullable = names.Remove("null");
                type = names.Count > 0 ? string.Join("|", names) : "null";
            }
            else
            {
                type = declared.GetString()!;
            }
        }

        if (type == "array" && schema.TryGetProperty("items", out var items))
        {
            type = $"array<{Describe(items)}>";
        }

        if (schema.TryGetProperty("format", out var format))
        {
            type += $"({format.GetString()})";
        }

        return type + (nullable ? "?" : "");
    }

    /// <summary>The media types a request or response body carries, and the schema behind each.</summary>
    private static string Body(JsonElement carrier)
    {
        if (!carrier.TryGetProperty("content", out var content))
        {
            return "-";
        }

        return string.Join(
            ", ",
            content.EnumerateObject()
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => c.Value.TryGetProperty("schema", out var schema)
                    ? $"{c.Name} {Describe(schema)}"
                    : c.Name));
    }

    // -- The comments in the source are the descriptions in the document (#126).
    //
    // Worth testing rather than assuming, because the failure is silent in both directions: the
    // build reports GenerateDocumentationFile as true whether or not the .xml ever reaches bin/, and
    // a document with no descriptions is a perfectly valid document. It is only wrong to a human, or
    // to the model on the other end of a generated tool surface.

    [Fact]
    public async Task Every_schema_property_carries_the_comment_written_beside_it()
    {
        var client = await _host.StartAsync();
        var document = await (await client.GetAsync(GleanvoltApi.DocumentPath)).ReadAsync();

        var undocumented = Schemas(document)
            .SelectMany(schema => Properties(schema.Value).Select(property => (schema.Key, property)))
            .Where(entry => !HasDescription(entry.property.Value))
            // ASP.NET Core's own type, documented by the framework and not by us.
            .Where(entry => entry.Key != "ProblemDetails")
            .Select(entry => $"{entry.Key}.{entry.property.Name}")
            .ToList();

        Assert.Empty(undocumented);
    }

    [Fact]
    public async Task Enums_carry_theirs_too()
    {
        // The case that was actually broken: every enum here is a Gleanvolt.Core type, and Core's
        // .xml was being written to obj/ and never copied beside its .dll -- so the generator had
        // nothing to read and eleven enums reached the document bare.
        var client = await _host.StartAsync();
        var document = await (await client.GetAsync(GleanvoltApi.DocumentPath)).ReadAsync();

        var bare = Schemas(document)
            .Where(schema => schema.Value.TryGetProperty("enum", out _))
            .Where(schema => !HasDescription(schema.Value))
            .Select(schema => schema.Key)
            .ToList();

        Assert.Empty(bare);
    }

    [Fact]
    public async Task The_reasoning_survives_the_trip_and_not_only_the_first_sentence()
    {
        // A spot check with teeth: this paragraph is three levels down -- a Core enum member's
        // <summary>, reached through a referenced assembly's XML file.
        var client = await _host.StartAsync();
        var document = await (await client.GetAsync(GleanvoltApi.DocumentPath)).ReadAsync();

        var mode = Schemas(document).Single(schema => schema.Key == "ChargeControlMode").Value;

        Assert.Contains("charger", mode.GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<KeyValuePair<string, JsonElement>> Schemas(JsonElement document) =>
        document.GetProperty("components").GetProperty("schemas").EnumerateObject()
            .Select(property => new KeyValuePair<string, JsonElement>(property.Name, property.Value));

    private static IEnumerable<JsonProperty> Properties(JsonElement schema) =>
        schema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject()
            : [];

    /// <summary>
    /// A description anywhere that counts. OpenAPI 3.1 renders a nullable reference as
    /// <c>oneOf: [null, $ref]</c> and hangs the property's own description on the branch rather than
    /// on the property, so the obvious test reports two dozen false positives.
    /// </summary>
    private static bool HasDescription(JsonElement schema)
    {
        if (schema.TryGetProperty("description", out var description)
            && !string.IsNullOrWhiteSpace(description.GetString()))
        {
            return true;
        }

        foreach (var keyword in (string[])["oneOf", "allOf", "anyOf"])
        {
            if (schema.TryGetProperty(keyword, out var branches)
                && branches.EnumerateArray().Any(HasDescription))
            {
                return true;
            }
        }

        return false;
    }
}
