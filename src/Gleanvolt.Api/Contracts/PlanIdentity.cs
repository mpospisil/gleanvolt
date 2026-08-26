using System.Globalization;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Contracts;

/// <summary>
/// The opaque token a quoted plan carries, and what can be read back out of it (issue #128).
///
/// <para><b>Self-describing rather than a key into a server-side basket.</b> A basket of quoted plans
/// is state: it needs a lifetime, an eviction policy, a size limit, and an answer for what a restart
/// does to it — all so that a caller can avoid re-sending four fields it already has. Everything this
/// token needs to say fits inside it, so nothing is stored and there is nothing to expire.</para>
///
/// <para><b>Advisory, and never a lock.</b> The only question it answers is "has the forecast moved
/// since you were shown this?", which is worth knowing before committing to a plan and is never a
/// reason to refuse one. An unreadable or absent token means "cannot tell", which is a perfectly good
/// answer and the one every caller that ignores this feature gets.</para>
/// </summary>
internal static class PlanIdentity
{
    /// <summary>The token for a plan: the forecast it was built on, and when.</summary>
    internal static string For(TargetedChargePlan plan) =>
        Encode($"f={plan.ForecastAsOf?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "none"}");

    /// <summary>
    /// When the forecast behind <paramref name="planId"/> was retrieved, or null when the token says
    /// nothing usable — malformed, absent, or quoted at a time no forecast was in hand.
    /// </summary>
    internal static DateTimeOffset? ForecastAsOf(string? planId)
    {
        if (Decode(planId) is not { } decoded || !decoded.StartsWith("f=", StringComparison.Ordinal))
        {
            return null;
        }

        var value = decoded[2..];

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;
    }

    // Base64url so the token survives a query string and a JSON field without escaping, and so that it
    // does not read as something a caller should parse or construct.
    private static string Encode(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Decode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);

            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (Exception)
        {
            // A token this build did not issue, or one somebody typed. "Cannot tell" is the answer.
            return null;
        }
    }
}
