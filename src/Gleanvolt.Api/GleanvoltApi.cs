using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

namespace Gleanvolt.Api;

/// <summary>
/// Registers and maps the HTTP API. Host-independent, exactly like the web UI's <c>WebUiHost</c>, so a
/// test host exercises the same routes, the same key check and the same document as production without
/// booting Modbus clients or an MQTT worker.
/// </summary>
public static class GleanvoltApi
{
    /// <summary>The OpenAPI document's name, and the version segment every route sits under.</summary>
    public const string DocumentName = "v1";

    /// <summary>Where the document is served, once the API is enabled.</summary>
    public const string DocumentPath = "/api/v1/openapi.json";

    private const string SecuritySchemeName = "apiKey";

    /// <summary>
    /// Fails fast on the one combination that cannot be honoured: the API switched on with no key to
    /// check against. Unlike the web UI — where "no password" sensibly means "no login", because it is
    /// a browser on a LAN with a person in front of it — an open control API that any program on the
    /// network can drive is never what somebody meant. Call before <c>Build()</c>.
    /// </summary>
    public static void ValidateKeyConfig(this ApiOptions api)
    {
        if (api.Enabled && !api.HasKeys)
        {
            throw new InvalidOperationException(
                "Api:Enabled is true but no Api:Keys are configured, which would leave the control API "
                + "open to anything that can reach the port. Set one out-of-band, e.g. "
                + "Api__Keys__my-client=$(openssl rand -hex 32), or leave Api:Enabled false.");
        }
    }

    /// <summary>Registers the API's services. Only meaningful when <see cref="ApiOptions.Enabled"/> is true.</summary>
    public static void AddGleanvoltApi(this IServiceCollection services, ApiOptions api)
    {
        if (!api.Enabled)
        {
            return;
        }

        // Enums cross the wire as their names, camel-cased, rather than as integers: a client generated
        // from this document gets a closed set of readable values, and a reordered enum member cannot
        // silently change what a stored request meant. Set on the app's minimal-API serializer because
        // that is the only JSON this process writes -- the UI is a Blazor circuit, not a REST client.
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        services.AddOpenApi(DocumentName, options =>
        {
            // Only this API. Everything else the host maps -- the Blazor endpoints, the login form --
            // is a page, not an operation, and has no business in a document a client is generated from.
            options.ShouldInclude = description => description.RelativePath?.StartsWith("api/", StringComparison.Ordinal) == true;

            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Gleanvolt",
                    Version = DocumentName,
                    Description =
                        "The local control API of a Gleanvolt installation: a SolaX hybrid inverter, a "
                        + "home battery, an EV charger and the roof above them.\n\n"
                        + "Everything is read from one poll loop on the local network — no cloud is "
                        + "involved and nothing here leaves the LAN. Powers are instantaneous watts "
                        + "signed as the hardware reports them (grid positive is import, battery "
                        + "positive is charging); energies are watt-hours at the charger or the meter, "
                        + "except in the energy-history endpoints, which are stated in kilowatt-hours "
                        + "per window. Every timestamp is ISO-8601 with an offset, because a departure "
                        + "time here is a local-time promise.\n\n"
                        + "Two of these endpoints write to hardware. Everything else observes.",
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[SecuritySchemeName] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description =
                        "One of the keys configured in Api:Keys, presented as 'Authorization: Bearer "
                        + "<key>'. The key's name is what appears in the log and in the recorded "
                        + "charging session as the source of an action.",
                };

                document.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(SecuritySchemeName, document)] = [],
                    },
                ];

                // Last, and after the schemas exist: the built-in generator has already put this
                // codebase's comments on every property it could, and this fills in the enums it
                // cannot reach (#126). Both assemblies, because the DTOs are this one's and every enum
                // on the wire is Core's.
                XmlDocumentationTransformer.DescribeEnums(
                    document, typeof(GleanvoltApi).Assembly, typeof(Core.Enums.ChargeControlMode).Assembly);

                return Task.CompletedTask;
            });
        });
    }

    /// <summary>
    /// Maps the API's routes and its document. Only meaningful when <see cref="ApiOptions.Enabled"/> is
    /// true; with it false nothing at all is mapped, so there is nothing to find rather than something
    /// that answers 401.
    /// </summary>
    public static void MapGleanvoltApi(this IEndpointRouteBuilder app, ApiOptions api, ILogger logger)
    {
        if (!api.Enabled)
        {
            return;
        }

        var keys = new ApiKeyFilter(api, logger);

        // Outside the group, so it answers without a key: a browser cannot send one, and an API that
        // says nothing at its own base URL is indistinguishable from one that is broken.
        app.MapIndex(DocumentPath);

        var group = app
            .MapGroup("/api/v1")
            .AddEndpointFilter(keys)
            .WithTags("Gleanvolt");

        group.MapSite();
        group.MapStatus();
        group.MapEnergy();
        group.MapSessions();
        group.MapForecast();
        group.MapPlans();
        group.MapControl();

        // Unauthenticated, like the index. It was behind the key at first, on the reasoning that there is
        // no point handing the shape of a control surface to a caller who cannot use it -- but the
        // document is what a client is *generated* from, and requiring a key to read it means it cannot
        // be opened in a browser or handed to a generator without one. The disclosure it avoided was
        // slight next to that: the 401 already announces the API exists, and on the same host the web UI
        // is open on the LAN by default with every control on it. The operations stay behind the key,
        // which is where the writes are.
        app.MapOpenApi("/api/{documentName}/openapi.json");

        logger.LogInformation(
            "HTTP API enabled on /api/{Document} with {KeyCount} key(s); the OpenAPI document is at {Path}.",
            DocumentName,
            api.Keys.Count(key => !string.IsNullOrWhiteSpace(key.Value)),
            DocumentPath);
    }
}
