using Gleanvolt.Api.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Gleanvolt.Api.Endpoints;

/// <summary>
/// The one endpoint that needs no key: what this is, where the document is, and how to authenticate.
///
/// <para>It exists because of what actually happens when somebody is handed a base URL — they open it
/// in a browser. Before this, <c>/api/v1/</c> was not a route at all and answered with an empty 404,
/// while every real route answered 401, because no browser can send an <c>Authorization</c> header.
/// A working API is indistinguishable from a broken one under those conditions.</para>
/// </summary>
internal static class IndexEndpoint
{
    internal static void MapIndex(this IEndpointRouteBuilder app, string documentPath)
    {
        app.MapGet(GleanvoltApi.BasePath, (EndpointDataSource endpoints, ApiHostInfo host) => Results.Ok(
            new ApiIndexResponse(
                Name: "Gleanvolt",
                Version: GleanvoltApi.DocumentName,
                Build: host.Version,
                Documentation: documentPath,
                Authentication: "Every endpoint below needs a key from Api:Keys, presented as "
                    + "'Authorization: Bearer <key>'. This index and the OpenAPI document do not.",
                Operations: Describe(endpoints))))
            .WithName("getIndex")
            .WithSummary("What this API is and how to authenticate")
            .WithDescription(
                "The entry point, and the only endpoint that needs no key. It lists the operations this "
                + "build serves and points at the OpenAPI document, so a base URL opened in a browser "
                + "says what it is instead of answering with an empty 404. It carries nothing an "
                + "unauthenticated caller could act on.")
            .Produces<ApiIndexResponse>();
    }

    /// <summary>
    /// The operations this build actually serves, read from the routing table rather than from a list
    /// kept by hand — a list that has to be maintained is a list that goes stale, and this one would
    /// go stale in the place a newcomer looks first.
    /// </summary>
    private static IReadOnlyList<ApiOperationResponse> Describe(EndpointDataSource endpoints) =>
    [
        .. endpoints.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                    .Select(method => new ApiOperationResponse(
                        method,
                        endpoint.RoutePattern.RawText!,
                        endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary)))
            .OrderBy(operation => operation.Path, StringComparer.Ordinal)
            .ThenBy(operation => operation.Method, StringComparer.Ordinal),
    ];
}
