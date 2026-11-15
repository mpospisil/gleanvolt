using Gleanvolt.Api.Contracts;
using Gleanvolt.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Gleanvolt.Api.Endpoints;

/// <summary>The external data providers (#386): each kind and its session.</summary>
internal static class ProviderEndpoints
{
    internal static void MapProviders(this IEndpointRouteBuilder api)
    {
        api.MapGet("/providers", ([FromServices] IExternalProviders providers) =>
                Results.Ok(providers.All.Select(ExternalProviderResponse.From).ToList()))
            .WithName("listExternalProviders")
            .WithSummary("The external data providers and their sessions")
            .WithDescription(
                "Each kind of data an outside program can supply over its SignalR hub on the web port, "
                + "whether it is on, whether a provider is connected, and when its data last arrived. "
                + "Kinds are turned on and their tokens made on Settings.")
            .Produces<List<ExternalProviderResponse>>();
    }
}
