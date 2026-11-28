using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The built-in car feed this installation used, which #477 removed, until a car plugin connects: the Car page names
/// the plugin to run, as Settings does for the weather (<see cref="IWeatherSourceSelection.BuiltInRetired"/>).
/// </summary>
public interface IRetiredCarFeed
{
    /// <summary>The plugin that replaces the built-in this installation used, or null once a car plugin has connected.</summary>
    CarFeedPlugin? Notice { get; }

    /// <summary>Raised when the notice goes.</summary>
    event Action? Changed;
}

/// <summary>A car plugin from gleanvolt-cars that replaces a built-in feed removed in #477.</summary>
/// <param name="Id">Its compose profile, folder in gleanvolt-cars and <c>GLEANVOLT_PLUGINS</c> name.</param>
/// <param name="Name">What the owner reads: "the {Name} plugin".</param>
/// <param name="Secret">What the owner enters on the plugin's page, which never carried over.</param>
public sealed record CarFeedPlugin(string Id, string Name, CarFeedSecret Secret)
{
    /// <summary>The volkswagen.de feed's replacement.</summary>
    public static CarFeedPlugin Volkswagen { get; } = new("volkswagen", "Volkswagen", CarFeedSecret.Password);

    /// <summary>The MyŠkoda feed's replacement.</summary>
    public static CarFeedPlugin Skoda { get; } = new("skoda", "MyŠkoda", CarFeedSecret.ApiKey);

    /// <summary>The VW Group Data Act portal's replacement.</summary>
    public static CarFeedPlugin VwGroup { get; } = new("vwgroup", "VW Group Data Act", CarFeedSecret.Password);

    /// <summary>The three, in the order a built-in was preferred.</summary>
    public static IReadOnlyList<CarFeedPlugin> All { get; } = [Volkswagen, Skoda, VwGroup];

    /// <summary>The plugin whose <see cref="Id"/> is <paramref name="id"/>, or null.</summary>
    public static CarFeedPlugin? Find(string? id) =>
        All.FirstOrDefault(plugin => string.Equals(plugin.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>What a car plugin's page asks for that Gleanvolt never handed over.</summary>
public enum CarFeedSecret
{
    /// <summary>The VW ID password.</summary>
    Password,

    /// <summary>A MyŠkoda Public API key.</summary>
    ApiKey,
}
