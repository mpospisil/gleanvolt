using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Plugins;

/// <summary>What a plugin says about itself: its status now, and when that changes.</summary>
public interface IPluginStatus
{
    /// <summary>The status to report, or null before there is one.</summary>
    ProviderStatus? Status { get; }

    /// <summary>Anything about the plugin changed; the status may have.</summary>
    event Action? Changed;
}

/// <summary>
/// Tells Gleanvolt the plugin's status: after every hello, since a new session starts with none, and whenever it
/// changes. A report that fails is forgotten, so the next change or hello says it again.
/// </summary>
public class PluginStatusReporter<TWelcome> : IHostedService
    where TWelcome : Welcome
{
    private readonly ProviderConnection<TWelcome> _connection;
    private readonly IPluginStatus _source;
    private readonly ILogger _logger;
    private ProviderStatus? _reported;

    public PluginStatusReporter(ProviderConnection<TWelcome> connection, IPluginStatus source, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(source);
        _connection = connection;
        _source = source;
        _logger = logger;

        // Here rather than at start: the first hello can come before it.
        connection.Welcomed += welcome => _ = ReportAsync(force: true);
        source.Changed += () => _ = ReportAsync(force: false);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ReportAsync(bool force)
    {
        if (_source.Status is not { } status || (!force && Same(status, _reported)))
        {
            return;
        }

        try
        {
            _reported = status;
            await _connection.ReportStatusAsync(status).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _reported = null;
            _logger.LogDebug("Could not report the status to Gleanvolt ({Reason}).", ex.Message);
        }
    }

    // The messages are a dictionary, so a record's equality would call every poll a change.
    private static bool Same(ProviderStatus status, ProviderStatus? other) =>
        other is not null && status.Healthy == other.Healthy && status.NeedsOwner == other.NeedsOwner
        && status.Code == other.Code && status.Message == other.Message;
}
