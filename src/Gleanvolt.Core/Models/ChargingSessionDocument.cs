namespace Gleanvolt.Core.Models;

/// <summary>
/// A whole session as one self-contained document: the header, every sample, every event.
///
/// <para>This is the <b>published</b> shape, deliberately separate from however the local store
/// happens to lay its tables out. The tables will churn as features are added; this must not, because
/// it is what a future upload writes to object storage (one object per session, keyed by date and id)
/// and what a future read API or web app consumes. Keeping the two apart is the difference between
/// adding a column and migrating a bucket.</para>
///
/// <para>A document is only ever produced for a <em>closed</em> session, which is immutable — so an
/// uploaded object never needs revisiting.</para>
/// </summary>
/// <param name="SchemaVersion">
/// Incremented whenever this document's shape changes in a way a consumer must notice. Present from
/// version 1 so that a reader never has to guess what it is looking at.
/// </param>
public sealed record ChargingSessionDocument(
    int SchemaVersion,
    ChargingSession Session,
    IReadOnlyList<ChargingSessionSample> Samples,
    IReadOnlyList<ChargingSessionEvent> Events)
{
    /// <summary>
    /// The version this build writes.
    ///
    /// <para>2 added the site-wide progress figures and the car's own SOC to every sample —
    /// <c>solarWh</c>, <c>forecastSolarWh</c>, <c>gridImportWh</c>, <c>vehicleSocPercent</c> and
    /// <c>vehicleSocCapturedAt</c>. Purely additive, so a v1 reader is unaffected; the bump is for the
    /// consumer that wants those and needs to know whether to expect them.</para>
    ///
    /// <para>3 added <c>dayForecast</c> to the header: the whole day's forecast curve, elapsed periods
    /// included, so a session can be read against the day it happened on without a forecast archive.
    /// Additive again — and nullable even at v3, since a day the controller only saw half of has only
    /// half a curve to give.</para>
    ///
    /// <para>4 added the weather: <c>weatherAtStart</c> and <c>weatherAtEnd</c> — the sky as a provider
    /// reported it when the session opened and when it closed — plus <c>sunrise</c> and <c>sunset</c>
    /// for the day it started on. Additive, and null throughout wherever no weather provider is
    /// configured, which is the default.</para>
    /// </summary>
    public const int CurrentSchemaVersion = 4;

    /// <summary>
    /// Builds a document at the current schema version.
    ///
    /// <para>A factory rather than a second constructor on purpose: a type with two constructors is one
    /// System.Text.Json refuses to deserialise, since it has no way to choose between them. That would
    /// break the round trip this type exists for, and it would break it at read time — in whatever
    /// consumes the published object, not here.</para>
    /// </summary>
    public static ChargingSessionDocument Create(
        ChargingSession session,
        IReadOnlyList<ChargingSessionSample> samples,
        IReadOnlyList<ChargingSessionEvent> events) =>
        new(CurrentSchemaVersion, session, samples, events);
}
