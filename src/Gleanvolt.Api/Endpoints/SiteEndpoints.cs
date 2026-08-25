using Gleanvolt.Api.Contracts;
using Gleanvolt.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;

namespace Gleanvolt.Api.Endpoints;

/// <summary>What this controller is connected to, as opposed to what it is doing.</summary>
internal static class SiteEndpoints
{
    internal static void MapSite(this IEndpointRouteBuilder api)
    {
        // Configuration resolved once at startup, so this needs no probe, no cache and no failure mode:
        // if the host is answering at all, the site it is answering about is the one it booted with.
        api.MapGet("/site", (PvSystemInfo site) => Results.Ok(SiteResponse.From(site)))
            .WithName("getSite")
            .WithSummary("The installation this controller speaks for")
            .WithDescription(
                "Which system, where it is, what the array does, and which devices it is made of. Ask "
                + "this first: every other endpoint reports what the controller is doing, and two "
                + "installations answer those identically. Values are null when unset rather than zero "
                + "— an unconfigured coordinate is not 0,0, which is a real place in the Atlantic.")
            .Produces<SiteResponse>();
    }
}
