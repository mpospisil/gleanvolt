using Gleanvolt.Api.Contracts;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gleanvolt.Api.Endpoints;

/// <summary>
/// The tariff (issue #265): what it is now, which strategies there are, and its settings — the same
/// settings Settings edits, through the same service, so the API cannot store what the page would refuse.
/// </summary>
internal static class TariffEndpoints
{
    internal static void MapTariff(this IEndpointRouteBuilder api)
    {
        api.MapGet("/tariff", (ITariffSchedule schedule, ITariffEditor editor, TimeProvider time) =>
                Results.Ok(TariffResponse.From(schedule, editor, time.GetUtcNow())))
            .WithName("getTariff")
            .WithSummary("The grid tariff now and as far ahead as it is known")
            .WithDescription(
                "The selected tariff strategy's low and high stretches, the rate at this moment and when "
                + "it next changes, with prices per kWh in the installation's currency. An empty timeline "
                + "means no tariff restriction, never 'always low': the provider is None, or the selected "
                + "one cannot answer, which 'unavailable' then explains.")
            .Produces<TariffResponse>();

        api.MapGet("/tariff/providers", (ITariffEditor editor) =>
            {
                var settings = editor.Read();
                return Results.Ok(editor.Providers
                    .Select(provider => new TariffProviderResponse(provider.Id, provider.Name, settings.Selects(provider.Id)))
                    .ToList());
            })
            .WithName("listTariffProviders")
            .WithSummary("The tariff strategies this build has")
            .WithDescription(
                "Each one is a way of knowing when the grid is cheap; 'provider' in the settings selects one "
                + "by its id. None means no tariff.")
            .Produces<List<TariffProviderResponse>>();

        api.MapGet("/tariff/settings", (ITariffEditor editor) => Results.Ok(TariffSettingsResponse.From(editor.Read())))
            .WithName("getTariffSettings")
            .WithSummary("The tariff settings as stored")
            .WithDescription(
                "The selection, the shared prices, and every strategy's own settings by key, including "
                + "those of a strategy that is not selected.")
            .Produces<TariffSettingsResponse>();

        api.MapPut("/tariff/settings", (TariffSettingsRequest body, ITariffEditor editor) =>
            {
                if (string.IsNullOrWhiteSpace(body.Provider))
                {
                    return ApiResults.BadRequest("'provider' is required: None for no tariff, or an id from GET /tariff/providers.");
                }

                var settings = editor.Read() with
                {
                    Provider = body.Provider.Trim(),
                    LowPricePerKwh = body.LowPricePerKwh,
                    HighPricePerKwh = body.HighPricePerKwh,
                };

                foreach (var (key, section) in body.Settings ?? new Dictionary<string, System.Text.Json.JsonElement>())
                {
                    settings = settings.WithSection(key, section);
                }

                var result = editor.Save(settings);

                return result.Saved
                    ? Results.Ok(TariffSettingsResponse.From(editor.Read(), result.Warnings))
                    : Results.Problem(
                        title: "Tariff not saved",
                        detail: string.Join(" ", result.Errors.Select(Describe)),
                        statusCode: StatusCodes.Status400BadRequest,
                        extensions: new Dictionary<string, object?>
                        {
                            ["problems"] = result.Problems.Select(TariffProblemResponse.From).ToList(),
                        });
            })
            .WithName("putTariffSettings")
            .WithSummary("Store the tariff settings, applied at once")
            .WithDescription(
                "Selects a strategy and sets the shared prices, and replaces the settings of each strategy "
                + "whose key is given; the others are kept. Checked by the same rules as Settings: a 400 "
                + "lists every problem under 'problems', each with its field and row, and nothing is "
                + "written. Warnings (a low price above the high one, no low-tariff periods) do not stop "
                + "the save and come back with it. Writes no hardware; the next poll reads the new tariff.")
            .Produces<TariffSettingsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static string Describe(TariffProblem problem) =>
        problem.Row is { } row ? $"{problem.Field} row {row + 1}: {problem.Message}" : $"{problem.Field}: {problem.Message}";
}
