using System.Text.RegularExpressions;
using Bunit;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Every password box can show what was typed into it, so a pasted API key or a new password can be
/// checked before it is saved. The toggle itself is js/reveal.js; what C# can check is the markup it
/// works on, and that no password box was left without it.
/// </summary>
public partial class RevealInputTests : BunitContext
{
    [Fact]
    public void Puts_the_toggle_beside_the_box_inside_one_wrapper()
    {
        var reveal = Render<RevealInput>(parameters => parameters
            .AddChildContent("<input id=\"secret\" type=\"password\" />"));

        var wrapper = reveal.Find(".reveal");
        Assert.NotNull(wrapper.QuerySelector("input#secret[type=password]"));

        var button = wrapper.QuerySelector("button.reveal-toggle")!;
        Assert.Equal("button", button.GetAttribute("type"));
        Assert.Equal("Reveal", button.QuerySelector(".reveal-show")!.TextContent);
        Assert.Equal("Hide", button.QuerySelector(".reveal-hide")!.TextContent);

        // The script owns the pressed state; a render that wrote it would undo a click on the next poll.
        Assert.Null(button.GetAttribute("aria-pressed"));
    }

    [Fact]
    public void A_disabled_box_gets_no_toggle()
    {
        var reveal = Render<RevealInput>(parameters => parameters
            .Add(p => p.Disabled, true)
            .AddChildContent("<input type=\"password\" disabled />"));

        Assert.Empty(reveal.FindAll("button"));
    }

    [Fact]
    public void Every_password_box_in_the_web_ui_has_the_toggle()
    {
        var components = Path.Combine(RepoRoot(), "src", "Gleanvolt.Web", "Components");
        var unwrapped = new List<string>();

        foreach (var file in Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (Match box in PasswordBox().Matches(text))
            {
                var before = text[..box.Index].TrimEnd();
                if (!Regex.IsMatch(before, "<RevealInput[^>]*>$"))
                {
                    unwrapped.Add($"{Path.GetFileName(file)}: {box.Value.Split('\n')[0].Trim()}");
                }
            }
        }

        Assert.Empty(unwrapped);
    }

    [GeneratedRegex("<(input|InputText)\\b[^\\n]*type=\"password\"")]
    private static partial Regex PasswordBox();

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Gleanvolt.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Gleanvolt.slnx not found above " + AppContext.BaseDirectory);
    }
}
