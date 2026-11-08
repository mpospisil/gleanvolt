using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The inverter driver's state on disk (issue #323): what a lasting change to the inverter needs to be
/// undone after a crash, so it must outlive the process and fail loudly.
/// </summary>
public sealed class DriverStateFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gleanvolt-driver-state-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "data", "inverter-state.json");

    private DriverStateFile Open() => new(FilePath, NullLogger<DriverStateFile>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void A_value_survives_into_the_next_process()
    {
        Open().Set("huawei:maxDischargePowerOriginal", "5000");

        Assert.Equal("5000", Open().Get("huawei:maxDischargePowerOriginal"));
    }

    [Fact]
    public void Removing_the_last_value_removes_the_file()
    {
        var store = Open();
        store.Set("a", "1");
        store.Set("b", "2");

        store.Set("a", null);
        Assert.True(File.Exists(FilePath));
        Assert.Equal("2", Open().Get("b"));

        store.Set("b", null);
        Assert.False(File.Exists(FilePath));
        Assert.Null(Open().Get("b"));
    }

    [Fact]
    public void An_unreadable_file_reads_as_empty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "not json");

        Assert.Null(Open().Get("a"));
    }

    [Fact]
    public void A_value_that_cannot_be_written_throws()
    {
        // A directory where the file should be: the write cannot land.
        Directory.CreateDirectory(FilePath);

        Assert.ThrowsAny<IOException>(() => Open().Set("a", "1"));
    }
}
