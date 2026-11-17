using Gleanvolt.Providers.Client;
using System.Text.Json;

namespace Gleanvolt.Plugin.Volkswagen.Volkswagen;

/// <summary>
/// The volkswagen.de account (#410): the VW ID, its password and the car's VIN. In Gleanvolt these are
/// <c>Vehicle:Website:Username</c>, <c>:Password</c> and <c>:Vin</c>, set on the Car page and kept in the
/// secret store; here the owner enters them on the plugin's page and they are kept in the data directory.
/// The password is never logged and never shown back.
/// </summary>
public sealed record VolkswagenAccount(string Username, string Password, string Vin)
{
    public static readonly VolkswagenAccount Empty = new(string.Empty, string.Empty, string.Empty);

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password) && !string.IsNullOrWhiteSpace(Vin);

    // Never the password, should a record ever reach a log line.
    public override string ToString() => $"{Username} ({Vin})";
}

/// <summary>Keeps the account in the data directory, through the SDK's secret store (#416).</summary>
public sealed class AccountStore(IProviderSecretStore files)
{
    private const string FileName = "account.json";

    private VolkswagenAccount? _account;

    /// <summary>
    /// Raised after the VW ID or the VIN changed: another account or another car, so the session, an
    /// open code challenge and the car's routing no longer apply. A new password alone does not raise it.
    /// </summary>
    public event Action? IdentityChanged;

    public VolkswagenAccount Current => _account ??= Load();

    /// <summary>
    /// Saves the account. An empty <paramref name="password"/> keeps the saved one, since the page never
    /// shows it back. Returns false when the file could not be written.
    /// </summary>
    public bool Save(string username, string? password, string vin)
    {
        var account = new VolkswagenAccount(
            username.Trim(),
            string.IsNullOrEmpty(password) ? Current.Password : password,
            vin.Trim().ToUpperInvariant());

        if (!files.Write(FileName, JsonSerializer.Serialize(account)))
        {
            return false;
        }

        var previous = Current;
        _account = account;

        if (!string.Equals(previous.Username, account.Username, StringComparison.OrdinalIgnoreCase) || previous.Vin != account.Vin)
        {
            IdentityChanged?.Invoke();
        }

        return true;
    }

    private VolkswagenAccount Load()
    {
        try
        {
            return files.Read(FileName) is { } json
                ? JsonSerializer.Deserialize<VolkswagenAccount>(json) ?? VolkswagenAccount.Empty
                : VolkswagenAccount.Empty;
        }
        catch (JsonException)
        {
            return VolkswagenAccount.Empty;
        }
    }
}
