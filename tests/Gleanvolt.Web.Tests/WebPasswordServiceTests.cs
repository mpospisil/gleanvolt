using System.Text.RegularExpressions;
using Gleanvolt.Web.Auth;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// #301: a forgotten password is reset with the recovery key or a one-time code from the host, never
/// with something the browser was sent; every reset replaces the key and signs everybody out.
/// </summary>
public sealed partial class WebPasswordServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gleanvolt-reset-").FullName;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturingLogger _log = new();
    private readonly FakePasswordStore _store = new();
    private readonly WebPasswordState _state = new(null);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CodeFile => Path.Combine(_root, "data", "password-reset-code.txt");

    private WebPasswordService Service() =>
        new(_state, _store, new PasswordResetOptions(CodeFile), _log, _clock);

    // What the host operator sees: the code as it appears in the log line.
    private string LoggedCode() => CodeInLog().Matches(string.Join('\n', _log.Lines)).Last().Groups[1].Value;

    [GeneratedRegex("code requested from the login page: ([A-Z0-9]{5}-[A-Z0-9]{5})")]
    private static partial Regex CodeInLog();

    private string Initial(WebPasswordService service, string password = "first password")
    {
        var result = service.SetInitial(password);
        Assert.True(result.Succeeded);
        return result.RecoveryKey!;
    }

    [Fact]
    public void The_first_password_is_stored_hashed_with_a_recovery_key()
    {
        var service = Service();

        var key = Initial(service);

        Assert.Matches("^([A-Z0-9]{5}-){4}[A-Z0-9]{5}$", key);
        Assert.True(WebPasswordHasher.Verify(_store.PasswordHash!, "first password"));
        Assert.True(WebPasswordHasher.Verify(_store.RecoveryKeyHash!, key.Replace("-", "", StringComparison.Ordinal)));
        Assert.Equal(_store.PasswordHash, _state.Hash);
        Assert.True(service.HasRecoveryKey);
    }

    [Fact]
    public void Setup_never_replaces_a_password_that_exists()
    {
        var service = Service();
        Initial(service);

        Assert.Equal(PasswordError.AlreadySet, service.SetInitial("second password").Error);
        Assert.True(WebPasswordHasher.Verify(_state.Hash, "first password"));
    }

    [Fact]
    public void A_password_that_cannot_be_saved_is_not_believed()
    {
        _store.Accept = false;

        var result = Service().SetInitial("first password");

        Assert.Equal(PasswordError.NotSaved, result.Error);
        Assert.False(_state.IsSet);
    }

    [Fact]
    public void The_recovery_key_resets_once_and_is_replaced()
    {
        var service = Service();
        var key = Initial(service);
        var stamp = _state.Stamp;

        // Typed back in lower case and without dashes: still the key.
        var reset = service.ResetWithRecoveryKey(key.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal), "second password");

        Assert.True(reset.Succeeded);
        Assert.NotEqual(key, reset.RecoveryKey);
        Assert.True(WebPasswordHasher.Verify(_state.Hash, "second password"));
        Assert.NotEqual(stamp, _state.Stamp);

        // The old key went with the old password.
        Assert.Equal(PasswordError.WrongKey, service.ResetWithRecoveryKey(key, "third password").Error);
    }

    [Fact]
    public void A_wrong_key_changes_nothing()
    {
        var service = Service();
        Initial(service);

        var result = service.ResetWithRecoveryKey("AAAAA-AAAAA-AAAAA-AAAAA-AAAAA", "second password");

        Assert.Equal(PasswordError.WrongKey, result.Error);
        Assert.True(WebPasswordHasher.Verify(_state.Hash, "first password"));
    }

    [Fact]
    public void A_password_set_by_hand_has_no_key_and_resets_with_a_code()
    {
        _state.Set(WebPasswordHasher.Hash("set by hand"));
        var service = Service();

        Assert.False(service.HasRecoveryKey);
        Assert.Equal(PasswordError.WrongKey, service.ResetWithRecoveryKey("anything", "second password").Error);

        Assert.Equal(CodeRequestResult.Issued, service.RequestCode());
        Assert.True(service.ResetWithCode(LoggedCode(), "second password").Succeeded);
        Assert.True(service.HasRecoveryKey);
    }

    [Fact]
    public void A_code_goes_to_the_log_and_an_owner_only_file_and_works_once()
    {
        var service = Service();
        Initial(service);

        Assert.Equal(CodeRequestResult.Issued, service.RequestCode());

        var code = LoggedCode();
        Assert.StartsWith(code, File.ReadAllText(CodeFile), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(CodeFile));
        }

        Assert.True(service.ResetWithCode(code, "second password").Succeeded);
        Assert.False(File.Exists(CodeFile));
        Assert.Equal(PasswordError.WrongCode, service.ResetWithCode(code, "third password").Error);
    }

    [Fact]
    public void A_code_expires_after_fifteen_minutes()
    {
        var service = Service();
        Initial(service);
        service.RequestCode();
        var code = LoggedCode();

        _clock.Advance(WebPasswordService.CodeLifetime);

        Assert.Equal(PasswordError.WrongCode, service.ResetWithCode(code, "second password").Error);
        Assert.True(WebPasswordHasher.Verify(_state.Hash, "first password"));
    }

    [Fact]
    public void Only_the_newest_code_is_valid()
    {
        var service = Service();
        Initial(service);
        service.RequestCode();
        var first = LoggedCode();

        _clock.Advance(WebPasswordService.CodeRequestInterval);
        service.RequestCode();
        var second = LoggedCode();

        Assert.NotEqual(first, second);
        Assert.Equal(PasswordError.WrongCode, service.ResetWithCode(first, "second password").Error);
        Assert.True(service.ResetWithCode(second, "second password").Succeeded);
    }

    [Fact]
    public void Codes_are_rate_limited()
    {
        var service = Service();
        Initial(service);

        Assert.Equal(CodeRequestResult.Issued, service.RequestCode());
        Assert.Equal(CodeRequestResult.TooSoon, service.RequestCode());

        for (var issued = 1; issued < WebPasswordService.CodeRequestsPerHour; issued++)
        {
            _clock.Advance(WebPasswordService.CodeRequestInterval);
            Assert.Equal(CodeRequestResult.Issued, service.RequestCode());
        }

        _clock.Advance(WebPasswordService.CodeRequestInterval);
        Assert.Equal(CodeRequestResult.TooSoon, service.RequestCode());

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CodeRequestResult.Issued, service.RequestCode());
    }

    [Fact]
    public void Too_many_wrong_attempts_lock_out_even_the_right_one_for_a_while()
    {
        var service = Service();
        var key = Initial(service);

        for (var attempt = 0; attempt < WebPasswordService.FailuresAllowed; attempt++)
        {
            service.ResetWithRecoveryKey("WRONG-WRONG", "second password");
        }

        Assert.Equal(PasswordError.TooManyAttempts, service.ResetWithRecoveryKey(key, "second password").Error);

        _clock.Advance(WebPasswordService.FailureWindow);
        Assert.True(service.ResetWithRecoveryKey(key, "second password").Succeeded);
    }

    [Fact]
    public void A_change_needs_the_current_password_and_replaces_the_key()
    {
        var service = Service();
        var key = Initial(service);

        Assert.Equal(PasswordError.WrongPassword, service.Change("not it", "second password").Error);

        var changed = service.Change("first password", "second password");

        Assert.True(changed.Succeeded);
        Assert.True(WebPasswordHasher.Verify(_state.Hash, "second password"));
        Assert.Equal(PasswordError.WrongKey, service.ResetWithRecoveryKey(key, "third password").Error);
    }

    [Fact]
    public void Every_reset_is_logged()
    {
        var service = Service();
        var key = Initial(service);

        service.ResetWithRecoveryKey(key, "second password");

        Assert.Contains(_log.Lines, line => line.Contains("reset with the recovery key", StringComparison.Ordinal)
            && line.Contains("signed out", StringComparison.Ordinal));
    }

    [Fact]
    public void A_short_password_is_refused_everywhere()
    {
        var service = Service();

        Assert.Equal(PasswordError.TooShort, service.SetInitial("short").Error);

        var key = Initial(service);
        Assert.Equal(PasswordError.TooShort, service.ResetWithRecoveryKey(key, "short").Error);
        Assert.True(WebPasswordHasher.Verify(_state.Hash, "first password"));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CapturingLogger : ILogger<WebPasswordService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
