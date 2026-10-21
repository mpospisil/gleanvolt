using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Tariff;

/// <summary>
/// The <see cref="ITariffSchedule"/> the controller and every surface read (issue #265): the saved
/// table expanded for today and tomorrow in the controller's time zone.
///
/// <para>Expanded once per local day and again after a save, rather than on every read: the poll loop,
/// the Home Assistant publisher and an open Overview all ask, and the answer only changes at midnight or
/// when the owner saves. A save applies straight away, with no restart.</para>
/// </summary>
public sealed class TariffSchedule : ITariffSchedule
{
    private readonly ITariffEditor _table;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private (DateOnly Day, TariffTimeline? Timeline)? _cached;

    public TariffSchedule(ITariffEditor table, TimeProvider time)
    {
        _table = table;
        _time = time;
        _table.Saved += Invalidate;
    }

    public TariffTimeline? Timeline
    {
        get
        {
            var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

            lock (_gate)
            {
                if (_cached is not { } cached || cached.Day != today)
                {
                    cached = (today, TariffExpansion.Expand(_table.Read(), _time.LocalTimeZone, today, _table.Currency));
                    _cached = cached;
                }

                return cached.Timeline;
            }
        }
    }

    private void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }
}
