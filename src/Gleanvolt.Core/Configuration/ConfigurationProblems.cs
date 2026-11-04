using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Configuration;

/// <summary>
/// What is wrong with a configuration, each problem in English naming its key — what startup prints
/// and refuses on — and, where it has one, a <see cref="Phrase"/> for the web UI to say the same thing
/// in its viewer's language (#292).
///
/// <para>A list of strings first, so everything that only ever needed the sentences — the startup
/// refusal, a log line, the API — reads it exactly as before.</para>
/// </summary>
public sealed class ConfigurationProblems : List<string>
{
    private readonly Dictionary<int, Phrase> _phrases = [];

    public ConfigurationProblems()
    {
    }

    public ConfigurationProblems(string english, Phrase phrase) => Add(english, phrase);

    /// <summary>A problem and the phrase that says it.</summary>
    public void Add(string english, Phrase phrase)
    {
        _phrases[Count] = phrase;
        Add(english);
    }

    /// <summary>The phrase for each problem, in the same order; null where one was added without.</summary>
    public IReadOnlyList<Phrase?> Phrases => [.. Enumerable.Range(0, Count).Select(index => _phrases.GetValueOrDefault(index))];

    /// <summary>Keys a form may not change from the web UI, one problem each.</summary>
    public static ConfigurationProblems NotEditable(IEnumerable<string> keys)
    {
        var problems = new ConfigurationProblems();
        foreach (var key in keys)
        {
            problems.Add($"{key} cannot be edited from the web UI.", Phrase.Of("Config.NotEditable", new PhraseArg.Text(key)));
        }

        return problems;
    }

    /// <summary>A host that would never read what the form saves.</summary>
    public static ConfigurationProblems NoOverridesFile() => new(
        "This host does not read a web UI overrides file, so an edit here would never be applied.",
        Phrase.Of("Config.NoOverridesFile"));

    /// <summary>The overrides file could not be written; <paramref name="error"/> is the system's own words.</summary>
    public static ConfigurationProblems NotWritten(string path, string error) => new(
        $"Could not write {path}: {error}",
        Phrase.Of("Config.NotWritten", new PhraseArg.Text(path), new PhraseArg.Text(error)));

    /// <summary>The phrases of <paramref name="problems"/> when it carries them, and none otherwise.</summary>
    public static IReadOnlyList<Phrase?> PhrasesOf(IReadOnlyList<string> problems) =>
        problems is ConfigurationProblems carried ? carried.Phrases : [];
}
