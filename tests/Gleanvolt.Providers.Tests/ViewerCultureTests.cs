using System.Globalization;
using Gleanvolt.Chargers;
using Xunit;

namespace Gleanvolt.Providers.Tests;

public sealed class ViewerCultureTests
{
    [Fact]
    public async Task WorkFromAnotherThreadRunsInTheViewersCulture()
    {
        var viewer = InCulture("cs", () => new ViewerCulture());
        string? seen = null;

        // The charger's poll raises the event on a thread of its own, in the service's culture.
        await Task.Run(() =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            viewer.Wrap(() => seen = CultureInfo.CurrentUICulture.Name)();
        });

        Assert.Equal("cs", seen);
    }

    [Fact]
    public async Task AsyncWorkRunsInTheViewersCulture()
    {
        var viewer = InCulture("cs", () => new ViewerCulture());
        string? seen = null;

        await Task.Run(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            await viewer.Wrap(() =>
            {
                seen = CultureInfo.CurrentUICulture.Name;
                return Task.CompletedTask;
            })();
        });

        Assert.Equal("cs", seen);
    }

    [Fact]
    public async Task TheCallingThreadKeepsItsOwnCulture()
    {
        var viewer = InCulture("cs", () => new ViewerCulture());
        string? after = null;

        await Task.Run(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

            // As InvokeAsync does: the work runs in an execution context of its own.
            await Task.Run(viewer.Wrap(() => { }));
            after = CultureInfo.CurrentUICulture.Name;
        });

        Assert.Equal(string.Empty, after);
    }

    private static T InCulture<T>(string name, Func<T> make)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            return make();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture);
        }
    }
}
