using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Layout;
using Gleanvolt.Web.Tests;

namespace Gleanvolt.Web.Screenshots;

/// <summary>
/// Renders a page for the user manual (#329): inside <see cref="MainLayout"/>, in English, signed in or
/// not, then written as a standalone HTML document that loads the web UI's own stylesheet and fonts from
/// <c>wwwroot</c>. No Blazor runs in the browser that takes the picture, so what it shows is what the
/// server rendered; the one script is <c>charts.js</c>, replaying the charts the page asked for.
/// </summary>
public abstract partial class ScreenshotTest : PageTest
{
    /// <summary>Where the HTML goes; unset, nothing is written and the test only renders.</summary>
    public const string OutputVariable = "GLEANVOLT_SCREENSHOTS";

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    private readonly BunitAuthorizationContext _auth;

    protected ScreenshotTest()
    {
        // The manual's pictures are in English, whatever the machine taking them is set to.
        CultureInfo.CurrentCulture = English;
        CultureInfo.CurrentUICulture = English;

        Services.AddSingleton(new ChargeControlStatusHolder());
        Services.AddSingleton(Options.Create(new RequestLocalizationOptions
        {
            SupportedUICultures = [.. WebUiCultures.Shipped.Select(CultureInfo.GetCultureInfo)],
        }));

        _auth = AddAuthorization();
        _auth.SetPolicies("Authenticated");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Renders the layout as nobody signed in sees it: the name and the language, no menu.</summary>
    protected void SignedOut() => _auth.SetNotAuthorized();

    /// <summary>Renders the layout as the owner sees it, with the menu.</summary>
    protected void SignedIn() => _auth.SetAuthorized("owner");

    /// <summary>
    /// <typeparamref name="TPage"/> inside the layout. Find and click on the result as on any rendered
    /// component; <see cref="Capture"/> takes what it shows at that moment.
    /// </summary>
    /// <param name="path">The page's address, so the menu marks the right link.</param>
    /// <param name="parameters">The page's parameters, by name.</param>
    protected IRenderedComponent<LayoutView> RenderPage<TPage>(string path, IReadOnlyDictionary<string, object?>? parameters = null)
        where TPage : IComponent
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(path);

        RenderFragment page = builder =>
        {
            builder.OpenComponent<TPage>(0);
            foreach (var (name, value) in parameters ?? new Dictionary<string, object?>())
            {
                builder.AddAttribute(1, name, value);
            }

            builder.CloseComponent();
        };

        return Render<LayoutView>(layout => layout
            .Add(view => view.Layout, typeof(MainLayout))
            .Add(view => view.ChildContent, page));
    }

    /// <summary>
    /// Writes what <paramref name="rendered"/> shows as <c>{name}.html</c>, with a <c>{name}.json</c>
    /// beside it that tells the capture script how to frame it.
    ///
    /// <para>The charts are drawn by <c>charts.js</c> in the browser, from data the page hands it through
    /// JS interop, which bUnit records. The last call for each chart is replayed in the document, with the
    /// web UI's own uPlot and <c>charts.js</c>, so a picture shows the chart the page would.</para>
    /// </summary>
    /// <param name="name">The PNG's name in the manual, e.g. <c>wizard-devices</c>.</param>
    /// <param name="selector">Only this element, not the whole page; null for the whole page.</param>
    /// <param name="width">The browser window's width, in CSS pixels.</param>
    protected void Capture<TComponent>(IRenderedComponent<TComponent> rendered, string name, string? selector = null, int width = 1200)
        where TComponent : IComponent
    {
        ArgumentNullException.ThrowIfNull(rendered);
        var markup = rendered.Markup;
        Assert.False(string.IsNullOrWhiteSpace(markup));

        if (Environment.GetEnvironmentVariable(OutputVariable) is not { Length: > 0 } output)
        {
            return;
        }

        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, name + ".html"), Document(BunitAttributes().Replace(markup, string.Empty), Charts()), Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(output, name + ".json"),
            JsonSerializer.Serialize(new { width, selector }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    // The last render of each chart, by the element it draws into, as calls a browser can make.
    private string Charts()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var call in JSInterop.Invocations)
        {
            if (!call.Identifier.StartsWith("solaxCharts.render", StringComparison.Ordinal) || call.Arguments.Count == 0)
            {
                continue;
            }

            var arguments = string.Join(", ", call.Arguments.Select(argument => JsonSerializer.Serialize(argument, argument?.GetType() ?? typeof(object), web)));
            latest[call.Arguments[0]?.ToString() ?? string.Empty] = $"{call.Identifier}({arguments});";
        }

        return string.Join(Environment.NewLine, latest.Values);
    }

    private static string Document(string markup, string charts)
    {
        var wwwroot = new Uri(Path.Combine(RepositoryRoot(), "src", "Gleanvolt.Web", "wwwroot") + Path.DirectorySeparatorChar).AbsoluteUri;
        var scripts = charts.Length == 0
            ? string.Empty
            : $$"""
                <script src="{{WebUtility.HtmlEncode(wwwroot)}}lib/uplot/uPlot.iife.min.js"></script>
                <script src="{{WebUtility.HtmlEncode(wwwroot)}}js/charts.js"></script>
                <script>
                window.addEventListener('load', function () {
                {{charts}}
                });
                </script>
                """;

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <link rel="stylesheet" href="{WebUtility.HtmlEncode(wwwroot)}css/site.css" />
            <link rel="stylesheet" href="{WebUtility.HtmlEncode(wwwroot)}lib/uplot/uPlot.min.css" />
            </head>
            <body>
            {markup}
            {scripts}
            </body>
            </html>
            """;
    }

    // bUnit's own bookkeeping (event handler numbers, element reference ids, which are new each run): no
    // browser reads them, and leaving them in would make the same page a different file every time.
    [GeneratedRegex(@"\s+blazor:[\w-]+(=""[^""]*"")?")]
    private static partial Regex BunitAttributes();

    private static string RepositoryRoot()
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
