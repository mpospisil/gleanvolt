using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Answers "if I started this now, what would happen?" — the targeted plan for a request that has
/// <b>not</b> been made, built from the same telemetry and the same forecast the poll loop is working
/// from.
///
/// <para>Read-only in the strongest sense: it sets no request, selects no mode, writes to no device
/// and touches none of the metering behind the running plan. A surface may call it on every keystroke
/// without consequence, which is exactly what makes it safe to put a plan in front of the owner
/// <em>before</em> the button that starts the charger rather than after it.</para>
///
/// <para>A separate seam from <see cref="ITargetedChargeSelector"/> on purpose. That one is the
/// promise; this one is the quote.</para>
/// </summary>
public interface ITargetedChargePreview
{
    /// <summary>
    /// The plan <paramref name="request"/> would produce if it were activated at the last telemetry
    /// reading, with nothing yet delivered against it.
    ///
    /// <para><c>null</c> when there is nothing to plan from — no poll has completed since startup, so
    /// there is no battery SOC, no house load and no instant to anchor to. A caller showing "no poll
    /// has completed yet" is the honest response; a plan built on zeroes is not.</para>
    /// </summary>
    TargetedChargePlan? Preview(TargetedChargeRequest request);
}
