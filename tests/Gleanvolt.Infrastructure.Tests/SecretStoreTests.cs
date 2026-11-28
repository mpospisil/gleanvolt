using System.Net;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Infrastructure.Secrets;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The one store all the bearer-equivalent secrets go through (issue #215): what it keeps, what it
/// promises, and — as much as it can — what it does not.
/// </summary>
public sealed class FileSecretStoreTests : IDisposable
{
    private const string Secret = "cookie-jar-or-api-key-0123456789";

    private readonly FileSecretStore _store = new(
        Path.Combine(Path.GetTempPath(), $"gleanvolt-secrets-{Guid.NewGuid():N}"));

    [Fact]
    public void A_written_secret_comes_back()
    {
        Assert.True(_store.Write("a-secret", Secret));
        Assert.Equal(Secret, _store.Read("a-secret"));
    }

    [Fact]
    public void Writing_again_replaces_it_rather_than_appending()
    {
        _store.Write("a-secret", Secret);
        _store.Write("a-secret", "the-newer-one");

        Assert.Equal("the-newer-one", _store.Read("a-secret"));
    }

    /// <summary>Deleting means deleting: nothing is left on disk holding the value.</summary>
    [Fact]
    public void Deleting_removes_it_from_disk()
    {
        _store.Write("a-secret", Secret);
        var path = _store.PathFor("a-secret");

        Assert.True(_store.Delete("a-secret"));

        Assert.Null(_store.Read("a-secret"));
        Assert.False(File.Exists(path));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_store.Directory),
            file => File.ReadAllText(file).Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void A_secret_that_was_never_there_reads_as_none_and_deletes_without_complaint()
    {
        Assert.Null(_store.Read("never-written"));
        Assert.True(_store.Delete("never-written"));
    }

    [Fact]
    public void A_missing_directory_reads_as_none_rather_than_failing()
    {
        var absent = new FileSecretStore(Path.Combine(Path.GetTempPath(), $"gleanvolt-absent-{Guid.NewGuid():N}"));

        Assert.Null(absent.Read("a-secret"));
        Assert.False(Directory.Exists(absent.Directory));
    }

    /// <summary>
    /// The one protection this store actually offers on a Pi: another unprivileged account on the box
    /// cannot read the car. Skipped rather than asserted on Windows, which has no such mode.
    /// </summary>
    [Fact]
    public void The_file_is_owner_only_where_the_platform_has_the_concept()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _store.Write("a-secret", Secret);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(_store.PathFor("a-secret")));
    }

    /// <summary>
    /// The rename is what makes the write atomic, and the temporary file it renames is the one place a
    /// secret could be left in the open. It is not left there.
    /// </summary>
    [Fact]
    public void The_write_leaves_no_temporary_file_behind()
    {
        _store.Write("a-secret", Secret);

        Assert.Equal([_store.PathFor("a-secret")], Directory.EnumerateFiles(_store.Directory).Order());
    }

    /// <summary>
    /// A write that cannot land is false, never an exception: the secret is in use in memory, and the
    /// caller's job is to say that a restart will ask for it again.
    /// </summary>
    [Fact]
    public void A_store_that_cannot_be_written_says_so_rather_than_throwing()
    {
        var blocked = new FileSecretStore(BlockedDirectory());

        Assert.False(blocked.Write("a-secret", Secret));
        Assert.Null(blocked.Read("a-secret"));
    }

    /// <summary>
    /// <c>Describe</c> is quoted on <c>/health</c> and in the startup log, where it is read by someone
    /// deciding where a backup goes. It must never be the word that makes that decision easy.
    /// </summary>
    [Fact]
    public void What_it_says_protects_the_secrets_never_claims_encryption()
    {
        Assert.DoesNotContain("encrypt", _store.Describe(), StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(_store.Describe());
    }

    /// <summary>Where a file has to be in the way of a directory, for the write-fails cases.</summary>
    internal static string BlockedDirectory()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"gleanvolt-blocked-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "a file where a directory was wanted");

        return Path.Combine(blocker, "data");
    }

    public void Dispose()
    {
        if (Directory.Exists(_store.Directory))
        {
            Directory.Delete(_store.Directory, recursive: true);
        }
    }
}

/// <summary>
/// Which store a deployment gets (issue #215). Windows is two deployments, not one, and getting that
/// wrong on the container side turns "my session expired" into "my session is gone forever" — so the
/// decision is a pure function, and these run on every platform including the Linux one it is
/// developed on.
/// </summary>
public class SecretStoreSelectionTests
{
    [Fact]
    public void Linux_gets_owner_only_files()
    {
        var choice = SecretStoreSelection.Choose(SecretStoreKind.Auto, isWindows: false, inContainer: false);

        Assert.Equal(SecretStoreKind.File, choice.Kind);
        Assert.Null(choice.Warning);
    }

    [Fact]
    public void Docker_on_linux_gets_owner_only_files()
    {
        Assert.Equal(
            SecretStoreKind.File,
            SecretStoreSelection.Choose(SecretStoreKind.Auto, isWindows: false, inContainer: true).Kind);
    }

    /// <summary>The zip and winget installs: <c>data\</c> sits inside a directory upgrades replace.</summary>
    [Fact]
    public void The_windows_install_gets_dpapi()
    {
        var choice = SecretStoreSelection.Choose(SecretStoreKind.Auto, isWindows: true, inContainer: false);

        Assert.Equal(SecretStoreKind.Dpapi, choice.Kind);
        Assert.Contains("Windows", choice.Reason);
    }

    /// <summary>
    /// The one that has to be right: a secret sealed to a Nano Server container's built-in account is
    /// unrecoverable the moment the container is recreated.
    /// </summary>
    [Fact]
    public void The_windows_container_does_not_get_dpapi_and_says_why()
    {
        var choice = SecretStoreSelection.Choose(SecretStoreKind.Auto, isWindows: true, inContainer: true);

        Assert.Equal(SecretStoreKind.File, choice.Kind);
        Assert.Contains("container", choice.Reason);
    }

    [Fact]
    public void Asking_for_files_on_windows_is_honoured()
    {
        Assert.Equal(
            SecretStoreKind.File,
            SecretStoreSelection.Choose(SecretStoreKind.File, isWindows: true, inContainer: false).Kind);
    }

    /// <summary>An explicit choice is honoured and argued with, rather than overruled.</summary>
    [Fact]
    public void Asking_for_dpapi_in_a_windows_container_is_honoured_with_a_warning()
    {
        var choice = SecretStoreSelection.Choose(SecretStoreKind.Dpapi, isWindows: true, inContainer: true);

        Assert.Equal(SecretStoreKind.Dpapi, choice.Kind);
        Assert.Contains("SECRETS__STORE=File", choice.Warning);
    }

    [Fact]
    public void Asking_for_dpapi_off_windows_is_refused_at_startup()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SecretStoreSelection.Choose(SecretStoreKind.Dpapi, isWindows: false, inContainer: false));

        Assert.Contains("only on Windows", error.Message);
    }

    [Fact]
    public void The_file_choice_builds_the_file_store()
    {
        var store = SecretStoreSelection.Create(
            new SecretStoreChoice(SecretStoreKind.File, "a test said so", null),
            Path.Combine(Path.GetTempPath(), $"gleanvolt-choice-{Guid.NewGuid():N}"));

        Assert.IsType<FileSecretStore>(store);
    }
}

/// <summary>
/// Windows DPAPI (issue #215). Skipped rather than asserted off Windows: <c>ProtectedData</c> is the
/// operating system's, and there is nothing to stand in for it — which is also why
/// <see cref="SecretStoreSelectionTests"/> carries the reasoning that can be checked anywhere.
/// </summary>
public sealed class DpapiSecretStoreTests : IDisposable
{
    private const string Secret = "cookie-jar-or-api-key-0123456789";

    private readonly FileSecretStore _files = new(
        Path.Combine(Path.GetTempPath(), $"gleanvolt-dpapi-{Guid.NewGuid():N}"));

    [Fact]
    public void A_sealed_secret_comes_back_and_is_not_on_disk_in_the_clear()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new DpapiSecretStore(_files);

        Assert.True(store.Write("a-secret", Secret));
        Assert.Equal(Secret, store.Read("a-secret"));
        Assert.DoesNotContain(Secret, File.ReadAllText(_files.PathFor("a-secret")), StringComparison.Ordinal);
    }

    [Fact]
    public void Overwriting_and_deleting_behave_as_they_do_for_files()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new DpapiSecretStore(_files);

        store.Write("a-secret", Secret);
        store.Write("a-secret", "the-newer-one");
        Assert.Equal("the-newer-one", store.Read("a-secret"));

        Assert.True(store.Delete("a-secret"));
        Assert.Null(store.Read("a-secret"));
        Assert.False(File.Exists(_files.PathFor("a-secret")));
    }

    [Fact]
    public void A_secret_that_was_never_there_reads_as_none()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Null(new DpapiSecretStore(_files).Read("never-written"));
    }

    /// <summary>An upgrade finds a plaintext file from before this store, reads it, and seals it.</summary>
    [Fact]
    public void A_plaintext_file_is_read_once_and_then_sealed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _files.Write("a-secret", Secret);

        var store = new DpapiSecretStore(_files);

        Assert.Equal(Secret, store.Read("a-secret"));
        Assert.DoesNotContain(Secret, File.ReadAllText(_files.PathFor("a-secret")), StringComparison.Ordinal);
        Assert.Equal(Secret, store.Read("a-secret"));
    }

    /// <summary>
    /// Truncated, or sealed by a Windows profile this machine no longer has — a restored backup, a
    /// rebuilt machine. <i>Sign in again</i>, never a crypto exception out of a startup path.
    /// </summary>
    [Theory]
    [InlineData("GV-DPAPI-1:not-base64-at-all")]
    [InlineData("GV-DPAPI-1:AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA")]
    [InlineData("GV-DPAPI-1:")]
    public void A_payload_that_will_not_unseal_is_no_secret_rather_than_an_exception(string payload)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _files.Write("a-secret", payload);

        Assert.Null(new DpapiSecretStore(_files).Read("a-secret"));
    }

    [Fact]
    public void What_it_says_protects_the_secrets_names_the_user_and_not_encryption()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var described = new DpapiSecretStore(_files).Describe();

        Assert.Contains("DPAPI", described);
        Assert.Contains("this user", described);
        Assert.DoesNotContain("encrypt", described, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_files.Directory))
        {
            Directory.Delete(_files.Directory, recursive: true);
        }
    }
}
