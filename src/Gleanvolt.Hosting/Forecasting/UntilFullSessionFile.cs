using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gleanvolt.Hosting.Forecasting;

/// <summary>
/// A Forecasted session running with <b>until full</b> on, kept on disk while it lasts (issue #279,
/// decision 1): the poll loop writes the file when it starts and deletes it when it ends. Since #413 every
/// mode is resumed after a restart from <see cref="Resume.ChargeModeFile"/>, and this file is left with
/// the day count; it still stands in for that file when upgrading from a version without it.
///
/// <para>The file holds when the session started, which is also what the day count is measured from:
/// day 1 is the day it was started, and a restart carries on counting rather than starting again.</para>
///
/// <para>A file that cannot be read is logged and treated as no session: the day count starts again.</para>
/// </summary>
public sealed class UntilFullSessionFile
{
    private readonly ILogger<UntilFullSessionFile> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private DateTimeOffset? _since;

    /// <param name="path">The absolute path of the file, whether or not it exists yet.</param>
    /// <param name="logger">Where restores, starts and ends are recorded.</param>
    /// <param name="timeProvider">The local time zone the days are counted in.</param>
    public UntilFullSessionFile(string path, ILogger<UntilFullSessionFile> logger, TimeProvider? timeProvider = null)
    {
        Path = path;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _since = Load();
        Restorable = _since is not null;
    }

    /// <summary>Where the session is kept.</summary>
    public string Path { get; }

    /// <summary>Whether the file named a running session when the service started.</summary>
    public bool Restorable { get; }

    /// <summary>When the running session started, or null when none is running.</summary>
    public DateTimeOffset? Since
    {
        get { lock (_gate) { return _since; } }
    }

    /// <summary>
    /// Called every poll: whether a Forecasted session with until full on is running now. Writes the file
    /// once when one starts and deletes it once when it ends; every other poll touches nothing.
    /// </summary>
    public void Track(bool running, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (running == _since is not null)
            {
                return;
            }

            if (running)
            {
                Write(now);
                _since = now;
                _logger.LogInformation("Forecasted until full started; kept in {Path} to count its days.", Path);
            }
            else
            {
                Delete();
                _since = null;
                _logger.LogInformation("Forecasted until full ended; {Path} removed.", Path);
            }
        }
    }

    /// <summary>Which day of the running session <paramref name="now"/> is, from 1; null when none is running.</summary>
    public int? Day(DateTimeOffset now)
    {
        if (Since is not { } since)
        {
            return null;
        }

        var zone = _timeProvider.LocalTimeZone;
        var started = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(since, zone).DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return Math.Max(1, today.DayNumber - started.DayNumber + 1);
    }

    private DateTimeOffset? Load()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path))?["since"]?.GetValue<DateTimeOffset>() is { } since)
            {
                return since;
            }

            _logger.LogWarning("{Path} names no session start, so the until-full day count starts again.", Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or FormatException)
        {
            _logger.LogWarning(
                "{Path} could not be read ({Reason}), so the until-full day count starts again.", Path, ex.Message);
        }

        return null;
    }

    // Atomically, as TariffSettingsFile writes: a temporary file beside it, then a rename. A failure is
    // logged and the session runs on regardless; it only loses the resume.
    private void Write(DateTimeOffset since)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, new JsonObject { ["mode"] = "Forecasted", ["untilFull"] = true, ["since"] = since }.ToJsonString());
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Could not write {Path} ({Reason}); Forecasted until full runs on, but a restart will not resume it.",
                Path,
                ex.Message);
        }
    }

    private void Delete()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "Could not remove {Path} ({Reason}); a restart may resume a Forecasted until full session that has ended.",
                Path,
                ex.Message);
        }
    }
}
