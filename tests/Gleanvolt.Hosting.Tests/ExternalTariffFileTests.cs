using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Tariff.External;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The external tariff kept across a restart (#401): a restarted Gleanvolt answers from the windows the
/// provider last sent, until it is back, instead of from no tariff, which would lift every
/// <i>low tariff only</i> restriction in between.
/// </summary>
public sealed class ExternalTariffFileTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<TariffWindow> Windows =
    [
        new(new TimeWindow(Now.AddHours(-2), Now.AddHours(3)), TariffRate.High),
        new(new TimeWindow(Now.AddHours(3), Now.AddHours(9)), TariffRate.Low, 2.10m),
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-external-tariff-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_root, "external-tariff.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // One run of Gleanvolt: a fresh strategy with the file attached, as the composition root makes it.
    private ExternalTariffProvider Start(DateTimeOffset now, string currency = "CZK")
    {
        var tariff = new ExternalTariffProvider();
        new ExternalTariffFile(FilePath, NullLogger<ExternalTariffFile>.Instance).Attach(tariff, currency, now);
        return tariff;
    }

    [Fact]
    public void APublishIsRestoredAfterARestart()
    {
        Start(Now).Publish(Windows, new TariffOrigin("Spot prices", Now));

        var restarted = Start(Now.AddMinutes(1));

        Assert.Equal(Windows, restarted.Windows);
        Assert.Equal(new TariffOrigin("Spot prices", Now), restarted.Origin);
    }

    [Fact]
    public void ARestoredTariffIsSavedAgainByTheNextPublish()
    {
        Start(Now).Publish(Windows, new TariffOrigin("Spot prices", Now));
        var restarted = Start(Now.AddMinutes(1));

        IReadOnlyList<TariffWindow> later = [new(new TimeWindow(Now, Now.AddHours(12)), TariffRate.Low)];
        restarted.Publish(later, new TariffOrigin("Spot prices", Now.AddMinutes(2)));

        Assert.Equal(later, Start(Now.AddMinutes(3)).Windows);
    }

    [Fact]
    public void WithNoFileThereIsNothingToRestore()
    {
        var tariff = Start(Now);

        Assert.Null(tariff.Windows);
    }

    [Fact]
    public void ATariffThatHasRunOutIsNotRestored()
    {
        Start(Now).Publish(Windows, new TariffOrigin("Spot prices", Now));

        Assert.Null(Start(Now.AddHours(9)).Windows);
    }

    [Fact]
    public void ATariffInAnotherCurrencyIsNotRestored()
    {
        Start(Now, "CZK").Publish(Windows, new TariffOrigin("Spot prices", Now));

        Assert.Null(Start(Now.AddMinutes(1), "EUR").Windows);
    }

    [Fact]
    public void AFileThatCannotBeReadIsIgnored()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, "{ not json");

        var tariff = Start(Now);

        Assert.Null(tariff.Windows);

        // And the next publish replaces it.
        tariff.Publish(Windows, new TariffOrigin("Spot prices", Now));
        Assert.Equal(Windows, Start(Now.AddMinutes(1)).Windows);
    }

    [Fact]
    public void TheFileIsReadable()
    {
        Start(Now).Publish(Windows, new TariffOrigin("Spot prices", Now));

        var text = File.ReadAllText(FilePath);

        Assert.Contains("\"rate\": \"low\"", text);
        Assert.Contains("\"currency\": \"CZK\"", text);
        Assert.Contains("\"name\": \"Spot prices\"", text);
    }
}
