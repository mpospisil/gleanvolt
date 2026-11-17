using Gleanvolt.Providers.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Gleanvolt.Providers.Tests;

/// <summary>The SDK's secret store (#416): what a provider keeps its account and session in.</summary>
public sealed class ProviderSecretStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gleanvolt-secrets-" + Guid.NewGuid().ToString("N"), "data");

    private ProviderSecretStore Store => new(_directory);

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_directory)!;

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_value_reads_back_and_is_replaced_by_the_next()
    {
        var store = Store;

        Assert.True(store.Write("account.json", "{\"username\":\"someone@example.com\"}"));
        Assert.True(store.Write("account.json", "{\"username\":\"someone.else@example.com\"}"));

        Assert.Equal("{\"username\":\"someone.else@example.com\"}", new ProviderSecretStore(_directory).Read("account.json"));
        Assert.Equal(["account.json"], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public void A_missing_value_is_null_and_deleting_it_is_fine()
    {
        Assert.Null(Store.Read("session.json"));
        Assert.True(Store.Delete("session.json"));
    }

    [Fact]
    public void A_deleted_value_is_gone()
    {
        Store.Write("session.json", "cookies");

        Assert.True(Store.Delete("session.json"));

        Assert.Null(Store.Read("session.json"));
    }

    [Fact]
    public void On_Linux_the_files_are_the_owners_alone_and_not_encrypted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Store.Write("session.json", "cookies");

        Assert.False(Store.IsEncrypted);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_directory, "session.json")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_directory));
    }

    /// <summary>The Volkswagen plugin's files from before the store (#410) are read as they are.</summary>
    [Fact]
    public void On_Linux_a_file_written_before_the_store_is_read()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "account.json"), "{\"vin\":\"WVGZZZE2ZMP000000\"}");

        Assert.Equal("{\"vin\":\"WVGZZZE2ZMP000000\"}", Store.Read("account.json"));
    }

    [Fact]
    public void On_Windows_the_file_holds_no_plain_text()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Store.Write("account.json", "a password");

        Assert.True(Store.IsEncrypted);
        Assert.DoesNotContain("a password", File.ReadAllText(Path.Combine(_directory, "account.json")));
        Assert.Equal("a password", Store.Read("account.json"));
    }

    [Fact]
    public void A_crash_mid_write_leaves_the_old_value()
    {
        Store.Write("session.json", "the old cookies");
        File.WriteAllText(Path.Combine(_directory, "session.json.tmp"), "half a new");

        Assert.Equal("the old cookies", Store.Read("session.json"));
        Assert.True(Store.Write("session.json", "the new cookies"));
        Assert.Equal("the new cookies", Store.Read("session.json"));
        Assert.False(File.Exists(Path.Combine(_directory, "session.json.tmp")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/dir")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData("session.json.tmp")]
    public void A_name_that_isnt_a_plain_file_name_is_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => Store.Write(name, "value"));
        Assert.Throws<ArgumentException>(() => Store.Read(name));
        Assert.Throws<ArgumentException>(() => Store.Delete(name));
    }

    [Fact]
    public void An_unwritable_directory_is_a_false_not_an_exception()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            Assert.False(Store.Write("session.json", "cookies"));
        }
        finally
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void It_is_registered_once_for_the_directory_given()
    {
        var services = new ServiceCollection().AddGleanvoltProviderSecrets(_directory).AddGleanvoltProviderSecrets("elsewhere");

        var store = services.BuildServiceProvider().GetRequiredService<IProviderSecretStore>();

        Assert.Equal(Path.GetFullPath(_directory), store.Directory);
    }
}
