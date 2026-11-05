namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The least configuration the controller starts on rather than going into setup mode (#301): both
/// devices, a location, a time zone that does not depend on the machine running the tests (CI is
/// UTC, which counts as unset) and a web UI password. A test that is about one of these overrides it.
/// </summary>
internal static class UsableSite
{
    // The shape of a PasswordHasher hash; nothing here verifies it.
    public const string PasswordHash = "AQAAAAIAAYagAAAAEPlaceholderHashForTestsOnly";

    public static readonly (string Key, string? Value)[] Settings =
    [
        ("Pv:Inverter:Host", "127.0.0.1"),
        ("Pv:Chargers:0:Host", "127.0.0.2"),
        ("Pv:Latitude", "50.0755"),
        ("Pv:Longitude", "14.4378"),
        ("Controller:TimeZone", "Europe/Prague"),
        ("Web:PasswordHash", PasswordHash),
    ];

    public static IEnumerable<KeyValuePair<string, string?>> Pairs =>
        Settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value));
}
