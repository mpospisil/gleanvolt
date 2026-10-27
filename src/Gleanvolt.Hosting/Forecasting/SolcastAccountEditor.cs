using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Solcast;

namespace Gleanvolt.Hosting.Forecasting;

/// <summary>
/// The Solcast account on Settings (issue #286): the store that holds it and the service that uses it,
/// behind the one interface the page needs.
///
/// <para><b>A save is a fetch.</b> There is no separate call to check the key: the fetch that puts a
/// newly saved account to use is the check, and its outcome is the status line. One call for both,
/// against a daily quota of about ten.</para>
/// </summary>
public sealed class SolcastAccountEditor : ISolcastAccountEditor
{
    private readonly SolcastAccountStore _account;
    private readonly SolcastForecastService _forecast;
    private readonly ILogger<SolcastAccountEditor> _logger;

    public SolcastAccountEditor(
        SolcastAccountStore account,
        SolcastForecastService forecast,
        ILogger<SolcastAccountEditor> logger)
    {
        _account = account;
        _forecast = forecast;
        _logger = logger;

        // Every fetch, the worker's included, is news to a page showing the status.
        _forecast.StatusChanged += () => Changed?.Invoke();
    }

    public event Action? Changed;

    public SolcastAccountSettings Read()
    {
        var current = _account.Current;

        return new SolcastAccountSettings(
            _account.Source,
            current?.ApiKey ?? string.Empty,
            current?.ResourceId ?? string.Empty,
            _account.EnvironmentConfigured,
            _account.Protection,
            _forecast.Status);
    }

    public async Task<SolcastAccountSaveResult> SaveAsync(
        string apiKey, string resourceId, CancellationToken cancellationToken = default)
    {
        var key = apiKey.Trim();
        var site = resourceId.Trim();
        var errors = new List<string>();

        // An empty key box keeps the key in use, so a corrected site does not mean pasting the key again.
        if (key.Length == 0)
        {
            key = _account.Current?.ApiKey ?? string.Empty;
        }

        if (key.Length == 0)
        {
            errors.Add("Enter the API key from your Solcast account.");
        }

        if (site.Length == 0)
        {
            errors.Add("Enter the rooftop site's resource id.");
        }

        if (errors.Count > 0)
        {
            return new SolcastAccountSaveResult(errors, Persisted: false);
        }

        var persisted = _account.Save(new SolcastAccount(key, site));

        if (!persisted)
        {
            _logger.LogWarning(
                "The Solcast account saved on Settings is in use, but the secret store would not keep it; a restart will forget it.");
        }

        _logger.LogInformation("Solcast account saved on Settings for rooftop site {ResourceId}; fetching the forecast with it.", site);

        await _forecast.RefreshAsync(cancellationToken).ConfigureAwait(false);

        return new SolcastAccountSaveResult([], persisted);
    }

    public void Remove()
    {
        _account.Clear();

        if (_account.Current is null)
        {
            // Nothing will ever refresh it now, so charge control must not go on planning against it.
            _forecast.Forget();
            _logger.LogInformation("Solcast account removed on Settings; no forecast is held any more.");
        }
        else
        {
            _logger.LogInformation(
                "Solcast account removed on Settings; the environment's account is in use from the next refresh.");
            Changed?.Invoke();
        }
    }
}
