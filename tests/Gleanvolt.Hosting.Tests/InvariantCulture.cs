using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Gleanvolt.Hosting.Tests;

internal static class InvariantCulture
{
    // What Gleanvolt.Worker's Program.cs does for the real process (#290): the tests assert on
    // invariant output, and must not start failing on a developer machine set to cs-CZ.
    [SuppressMessage("Usage", "CA2255", Justification = "The test assembly's equivalent of the host's first lines.")]
    [ModuleInitializer]
    internal static void Pin()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
    }
}
