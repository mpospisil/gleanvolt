using Gleanvolt.Core.Configuration;

namespace Gleanvolt.Core.Models;

/// <summary>
/// The settings a first-run setup still has to collect (#301), and nothing else: whether this
/// installation is usable is a fact about its configuration, so it is computed from that
/// configuration every time it is asked, never stored as a flag that could disagree with it.
///
/// <para><b>Required</b> is the minimum the controller needs to run: the web UI's password, the
/// inverter and the charger. Anything else that makes the configuration unsafe to run on is required
/// too: a key that has moved, a latitude without its longitude, a time zone this host cannot resolve.
/// Until every required item is in place the controller does not start — with the web UI on the host
/// comes up in setup mode instead; headless it refuses to start.</para>
///
/// <para><b>Recommended</b> is what the controller runs without, but runs worse without: where the site
/// is (no weather without it) and which time zone it lives in (UTC days without it on Windows). Listed
/// so the UI can ask for them, and never block the start.</para>
///
/// <para><b>Optional</b> is what a working installation can do without: the Solcast forecast, the
/// car, a tariff, the MQTT link and the weather. Missing ones are listed so the UI can offer them,
/// and never block anything.</para>
/// </summary>
public sealed record SetupStatus(
    IReadOnlyList<SetupProblem> MissingRequired,
    IReadOnlyList<OptionalSetting> MissingOptional,
    IReadOnlyList<SetupProblem>? MissingRecommended = null)
{
    /// <summary>The recommended settings still missing; never null.</summary>
    public IReadOnlyList<SetupProblem> Recommended => MissingRecommended ?? [];

    /// <summary>Every required setting is set and valid: the controller may run.</summary>
    public bool Usable => MissingRequired.Count == 0;

    /// <summary>The required problems as one refusal, in the shape the resolver has always thrown.</summary>
    public ConfigurationProblems Problems
    {
        get
        {
            var problems = new ConfigurationProblems();
            foreach (var problem in MissingRequired)
            {
                if (problem.Phrase is { } phrase)
                {
                    problems.Add(problem.English, phrase);
                }
                else
                {
                    problems.Add(problem.English);
                }
            }

            return problems;
        }
    }
}

/// <summary>One required setting that is missing or wrong, in English and as a translatable phrase.</summary>
public sealed record SetupProblem(SetupArea Area, string English, Phrase? Phrase);

/// <summary>Which part of a setup a problem belongs to.</summary>
public enum SetupArea
{
    Configuration,
    Password,
    Inverter,
    Charger,
    Location,
    TimeZone,
    Site,
}

/// <summary>The settings an installation can run without.</summary>
public enum OptionalSetting
{
    Solcast,
    Ev,
    Tariff,
    Mqtt,
    Weather,
}
