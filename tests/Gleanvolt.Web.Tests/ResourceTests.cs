using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The UI's resources are complete and consistent (#290). Read from the source tree rather than the
/// built assembly, so a failure names the file a translator has to fix.
/// </summary>
public sealed partial class ResourceTests
{
    private static readonly string ResourcesDirectory = FindResources();

    public static TheoryData<string> Neutral() => [.. Files(translated: false)];


    [Fact]
    public void There_is_at_least_one_resource() =>
        Assert.NotEmpty(Files(translated: false));

    // Facts over every translation rather than theories over each: until the first language lands
    // there are none, and xUnit fails a theory with no data.
    [Fact]
    public void A_translation_has_exactly_the_keys_of_its_neutral_file()
    {
        var problems =
            from file in Files(translated: true)
            let neutral = Read(NeutralOf(file)).Keys
            let translated = Read(file).Keys
            from problem in neutral.Except(translated).Select(key => $"{file} is missing {key}")
                .Concat(translated.Except(neutral).Select(key => $"{file} has {key}, which {NeutralOf(file)} no longer does"))
            select problem;

        Assert.Empty(problems);
    }

    [Fact]
    public void A_translation_keeps_every_placeholder()
    {
        var broken =
            from file in Files(translated: true)
            let neutral = Read(NeutralOf(file))
            from entry in Read(file)
            where neutral.TryGetValue(entry.Key, out var english) && !Placeholders(english).SetEquals(Placeholders(entry.Value))
            select $"{file}: {entry.Key}";

        Assert.Empty(broken);
    }

    [Fact]
    public void A_translation_is_for_a_shipped_language() =>
        Assert.Empty(Files(translated: true).Where(file => !WebUiCultures.Shipped.Contains(CultureOf(file))));

    [Fact]
    public void Every_shipped_language_has_every_resource()
    {
        var missing =
            from culture in WebUiCultures.Shipped.Skip(1)
            from neutral in Files(translated: false)
            let translation = $"{Path.GetFileNameWithoutExtension(neutral)}.{culture}.resx"
            where !File.Exists(Path.Combine(ResourcesDirectory, translation))
            select translation;

        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Neutral))]
    public void A_counted_phrase_has_all_its_forms(string file)
    {
        var keys = Read(file).Keys.ToHashSet();

        var incomplete = keys
            .Select(key => PluralSuffix().Match(key))
            .Where(match => match.Success)
            .Select(match => match.Groups["stem"].Value)
            .Distinct()
            .Where(stem => !keys.Contains(stem + "_One") || !keys.Contains(stem + "_Few") || !keys.Contains(stem + "_Other"));

        Assert.Empty(incomplete);
    }

    [Theory]
    [MemberData(nameof(Neutral))]
    public void No_neutral_string_is_empty(string file) =>
        Assert.Empty(Read(file).Where(entry => string.IsNullOrWhiteSpace(entry.Value)).Select(entry => entry.Key));

    private static IEnumerable<string> Files(bool translated) =>
        Directory.GetFiles(ResourcesDirectory, "*.resx")
            .Where(path => CultureOf(path) is not null == translated)
            .Select(path => Path.GetFileName(path))
            .Order();

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(Path.Combine(ResourcesDirectory, file))
            .Root!.Elements("data")
            .ToDictionary(data => (string)data.Attribute("name")!, data => (string?)data.Element("value") ?? string.Empty);

    private static HashSet<string> Placeholders(string value) =>
        [.. Placeholder().Matches(value).Select(match => match.Groups[1].Value)];

    // SharedText.cs.resx -> "cs"; SharedText.resx -> null.
    private static string? CultureOf(string path) =>
        Path.GetFileNameWithoutExtension(path).Split('.') is [_, var culture] ? culture : null;

    private static string NeutralOf(string file) =>
        Path.GetFileNameWithoutExtension(file).Split('.')[0] + ".resx";

    private static string FindResources()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Gleanvolt.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "Gleanvolt.Web", "Resources");
            }
        }

        throw new InvalidOperationException("Gleanvolt.slnx not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\{(\d+)(?:[:,][^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^(?<stem>.+)_(One|Few|Other)$")]
    private static partial Regex PluralSuffix();
}
