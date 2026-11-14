namespace Gleanvolt.Providers.Contracts.Tariff;

/// <summary>The low-tariff kind's contract version, and where its hub is.</summary>
public static class TariffContract
{
    /// <summary>The kind's id, also the token's kind.</summary>
    public const string Kind = "tariff";

    /// <summary>Changes only when an older provider would be misread.</summary>
    public const int Major = 1;

    /// <summary>Grows with every addition an older provider can ignore.</summary>
    public const int Minor = 0;

    /// <summary>The hub's path on Gleanvolt's web port.</summary>
    public const string HubPath = "/providers/tariff";
}

/// <summary>
/// The tariff hub: a provider publishes when the grid is cheap. Gleanvolt keeps the last accepted data
/// and acts on it until its last window ends, connected or not.
/// </summary>
public interface ITariffHub : IProviderHub<TariffWelcome>
{
    /// <summary>
    /// Replaces the data Gleanvolt holds, after it passes every check; on any problem nothing changes
    /// and the problems come back.
    /// </summary>
    Task<PublishResult> Publish(TariffData data);
}

/// <summary>What the tariff hub may send its provider.</summary>
public interface ITariffClient : IProviderClient
{
    /// <summary>
    /// Gleanvolt would like fresh data. Answer with <see cref="ITariffHub.Publish"/>, quoting
    /// <see cref="RefreshRequest.Id"/> in <see cref="TariffData.InReplyTo"/>.
    /// </summary>
    Task RefreshRequested(RefreshRequest request);
}

/// <summary>An accepted tariff hello: what the provider needs to build windows Gleanvolt can use.</summary>
public sealed record TariffWelcome : Welcome
{
    /// <summary>The installation's IANA time zone, e.g. "Europe/Prague": what "22:00" means there.</summary>
    public string TimeZone { get; init; } = string.Empty;

    /// <summary>The ISO 4217 code every price must be in, e.g. "CZK".</summary>
    public string Currency { get; init; } = string.Empty;
}

/// <summary>Which price applies.</summary>
public enum TariffRate
{
    /// <summary>Cheap: what the low-tariff-only options wait for.</summary>
    Low,

    /// <summary>Expensive.</summary>
    High,
}

/// <summary>One stretch on a single rate, half-open: from <see cref="Start"/> up to, not including, <see cref="End"/>.</summary>
/// <param name="Start">In UTC.</param>
/// <param name="End">In UTC, after <paramref name="Start"/>.</param>
/// <param name="Rate">Low or high.</param>
/// <param name="PricePerKwh">
/// This window's own price in <see cref="TariffWelcome.Currency"/>, or null for the owner's shared price
/// of <paramref name="Rate"/>. May be negative.
/// </param>
public sealed record TariffWindow(DateTimeOffset Start, DateTimeOffset End, TariffRate Rate, decimal? PricePerKwh = null);

/// <summary>A tariff as concrete instants.</summary>
/// <param name="IssuedAt">When the provider made it.</param>
/// <param name="InReplyTo">The request this answers, or null when sent unprompted.</param>
/// <param name="Currency">Must equal <see cref="TariffWelcome.Currency"/>.</param>
/// <param name="Windows">
/// In time order, each starting where the one before ends, covering the moment of publishing and at
/// most 7 days ahead. Neighbours on the same rate with the same price are joined; on the same rate with
/// different prices they are refused.
/// </param>
public sealed record TariffData(
    DateTimeOffset IssuedAt,
    RequestId? InReplyTo,
    string Currency,
    IReadOnlyList<TariffWindow> Windows);
