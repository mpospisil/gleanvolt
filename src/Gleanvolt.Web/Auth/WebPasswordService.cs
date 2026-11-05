using System.Security.Cryptography;
using System.Text;
using Gleanvolt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Web.Auth;

/// <summary>
/// Every way the web UI's one password is set or replaced (#301): the first time, from Settings by
/// someone who knows the current one, and from the login page by someone who has forgotten it.
///
/// <para><b>Forgetting it is not a lockout.</b> There is no email to send a link to and no second
/// account to ask, so a reset proves something else instead: either the recovery key, shown once when
/// the password was set, or access to the host itself. A one-time code is written to the log and to an
/// owner-only file in the data directory, and never to the browser: whoever can read it could already
/// change the configuration, so it grants nothing new.</para>
///
/// <para><b>Every reset signs everybody out.</b> The cookie carries a stamp of the password hash (see
/// <see cref="PasswordStamp"/>), so a new hash invalidates every session issued under the old one. And
/// every reset issues a new recovery key, so the old one is spent with the password it belonged to.</para>
///
/// <para>Thread-safe: requests come from concurrent HTTP requests and circuits, and the code, the
/// rate limits and the password itself change together under one lock.</para>
/// </summary>
public sealed class WebPasswordService
{
    /// <summary>The shortest password accepted. A floor rather than a policy; see the setup page.</summary>
    public const int MinimumLength = 8;

    /// <summary>How long a one-time code stays valid.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);

    /// <summary>The shortest interval between two code requests.</summary>
    public static readonly TimeSpan CodeRequestInterval = TimeSpan.FromMinutes(1);

    /// <summary>How many codes may be requested in an hour.</summary>
    public const int CodeRequestsPerHour = 5;

    /// <summary>How many wrong keys, codes or current passwords are tolerated in <see cref="FailureWindow"/>.</summary>
    public const int FailuresAllowed = 10;

    /// <summary>The window <see cref="FailuresAllowed"/> is counted over.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    // No 0/O, 1/I/L: both are read off a screen or a log and typed back in.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly WebPasswordState _state;
    private readonly IWebPasswordStore _store;
    private readonly PasswordResetOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<WebPasswordService> _logger;
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private readonly Queue<DateTimeOffset> _codeRequests = new();

    private PendingCode? _code;

    public WebPasswordService(
        WebPasswordState state,
        IWebPasswordStore store,
        PasswordResetOptions options,
        ILogger<WebPasswordService> logger,
        TimeProvider? time = null)
    {
        _state = state;
        _store = store;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Whether the password in force has a recovery key that can reset it.</summary>
    public bool HasRecoveryKey => _state.RecoveryKeyHash.Length > 0;

    /// <summary>Where a requested one-time code is written, for the page to name.</summary>
    public string CodeFilePath => _options.CodeFilePath;

    /// <summary>Sets the first password. Refused once one exists: replacing it is a change or a reset.</summary>
    public PasswordChangeResult SetInitial(string password)
    {
        lock (_gate)
        {
            if (_state.IsSet)
            {
                return PasswordChangeResult.Failed(PasswordError.AlreadySet);
            }

            return Replace(password, "set on the setup page", signsOut: false);
        }
    }

    /// <summary>Replaces the password for someone who knows the current one.</summary>
    public PasswordChangeResult Change(string current, string next)
    {
        lock (_gate)
        {
            if (Throttled())
            {
                return PasswordChangeResult.Failed(PasswordError.TooManyAttempts);
            }

            if (!WebPasswordHasher.Verify(_state.Hash, current ?? ""))
            {
                Fail("A password change from Settings was refused: the current password was wrong.");
                return PasswordChangeResult.Failed(PasswordError.WrongPassword);
            }

            return Replace(next, "changed from Settings", signsOut: true);
        }
    }

    /// <summary>Replaces a forgotten password with the recovery key.</summary>
    public PasswordChangeResult ResetWithRecoveryKey(string recoveryKey, string next)
    {
        lock (_gate)
        {
            if (Throttled())
            {
                return PasswordChangeResult.Failed(PasswordError.TooManyAttempts);
            }

            if (!HasRecoveryKey || !WebPasswordHasher.Verify(_state.RecoveryKeyHash, Normalise(recoveryKey)))
            {
                Fail("A password reset was refused: the recovery key was wrong.");
                return PasswordChangeResult.Failed(PasswordError.WrongKey);
            }

            if (!LongEnough(next))
            {
                return PasswordChangeResult.Failed(PasswordError.TooShort);
            }

            return Replace(next, "reset with the recovery key", signsOut: true);
        }
    }

    /// <summary>
    /// Issues a one-time code, written to the log and to <see cref="CodeFilePath"/>. Only the newest
    /// code is valid. The code itself is never returned: the browser that asked must not receive it.
    /// </summary>
    public CodeRequestResult RequestCode()
    {
        lock (_gate)
        {
            if (!_state.IsSet)
            {
                return CodeRequestResult.NoPassword;
            }

            var now = _time.GetUtcNow();

            while (_codeRequests.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromHours(1))
            {
                _codeRequests.Dequeue();
            }

            if (_codeRequests.Count >= CodeRequestsPerHour
                || _codeRequests.Count > 0 && now - _codeRequests.Last() < CodeRequestInterval)
            {
                _logger.LogWarning("A password reset code was requested again too soon; none was issued.");
                return CodeRequestResult.TooSoon;
            }

            var code = Random(10);
            var expires = now + CodeLifetime;

            try
            {
                WriteCodeFile(code, expires);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log still has it, so the request is not wasted -- but say the file could not be.
                _logger.LogError(ex, "The password reset code could not be written to {Path}.", _options.CodeFilePath);
            }

            _code = new PendingCode(Digest(Normalise(code)), expires);
            _codeRequests.Enqueue(now);

            // Warning, so it is in the log at every level a deployment might set. The code is the
            // point of the line: whoever reads this log can already change the configuration.
            _logger.LogWarning(
                "Password reset code requested from the login page: {Code}. Valid once, until {Expires:HH:mm} "
                + "UTC; requesting another replaces it. Also written to {Path}.",
                Format(code),
                expires,
                _options.CodeFilePath);

            return CodeRequestResult.Issued;
        }
    }

    /// <summary>Replaces a forgotten password with a one-time code from the host.</summary>
    public PasswordChangeResult ResetWithCode(string code, string next)
    {
        lock (_gate)
        {
            if (Throttled())
            {
                return PasswordChangeResult.Failed(PasswordError.TooManyAttempts);
            }

            var now = _time.GetUtcNow();

            if (_code is not { } pending
                || now >= pending.Expires
                || !CryptographicOperations.FixedTimeEquals(pending.Digest, Digest(Normalise(code))))
            {
                Fail("A password reset was refused: the one-time code was wrong, expired or already used.");
                return PasswordChangeResult.Failed(PasswordError.WrongCode);
            }

            if (!LongEnough(next))
            {
                return PasswordChangeResult.Failed(PasswordError.TooShort);
            }

            var result = Replace(next, "reset with a one-time code", signsOut: true);

            if (result.Succeeded)
            {
                // Once: spent whether or not anything else goes wrong afterwards.
                _code = null;
                DeleteCodeFile();
            }

            return result;
        }
    }

    // Under _gate. Persisted before it is believed, as the setup page always has: a hash held only in
    // memory would quietly stop existing on the next restart.
    private PasswordChangeResult Replace(string password, string how, bool signsOut)
    {
        if (!LongEnough(password))
        {
            return PasswordChangeResult.Failed(PasswordError.TooShort);
        }

        var recoveryKey = Random(25);
        var hash = WebPasswordHasher.Hash(password);
        var recoveryKeyHash = WebPasswordHasher.Hash(recoveryKey);

        if (!_store.Save(hash, recoveryKeyHash))
        {
            return PasswordChangeResult.Failed(PasswordError.NotSaved);
        }

        _state.Set(hash, recoveryKeyHash);
        _failures.Clear();

        _logger.LogWarning(
            signsOut
                ? "The web UI password was {How}. Every signed-in session has been signed out, and a new "
                  + "recovery key was issued; the old one no longer works."
                : "The web UI password was {How}, and its recovery key was issued.",
            how);

        return PasswordChangeResult.Changed(Format(recoveryKey));
    }

    private bool Throttled()
    {
        var now = _time.GetUtcNow();

        while (_failures.TryPeek(out var oldest) && now - oldest >= FailureWindow)
        {
            _failures.Dequeue();
        }

        return _failures.Count >= FailuresAllowed;
    }

    private void Fail(string message)
    {
        _failures.Enqueue(_time.GetUtcNow());
        _logger.LogWarning("{Message}", message);
    }

    private static bool LongEnough(string? password) => (password ?? "").Length >= MinimumLength;

    private void WriteCodeFile(string code, DateTimeOffset expires)
    {
        var directory = Path.GetDirectoryName(_options.CodeFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var open = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            open.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var writer = new StreamWriter(new FileStream(_options.CodeFilePath, open));
        writer.WriteLine(Format(code));
        writer.WriteLine();
        writer.WriteLine(System.FormattableString.Invariant(
            $"Gleanvolt password reset code. Valid once, until {expires:yyyy-MM-dd HH:mm} UTC."));
        writer.WriteLine("Enter it on the login page under \"Forgot password\". Requesting another code replaces this one.");
    }

    private void DeleteCodeFile()
    {
        try
        {
            File.Delete(_options.CodeFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The used password reset code could not be removed from {Path}.", _options.CodeFilePath);
        }
    }

    private static string Random(int length) =>
        string.Create(length, 0, (span, _) =>
        {
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });

    // Groups of five, for reading aloud and typing back. Normalise undoes it, and forgives case.
    private static string Format(string value) =>
        string.Join('-', Enumerable.Range(0, (value.Length + 4) / 5)
            .Select(group => value.Substring(group * 5, Math.Min(5, value.Length - (group * 5)))));

    private static string Normalise(string? value) =>
        new([.. (value ?? "").ToUpperInvariant().Where(character => character is not ('-' or ' '))]);

    private static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private sealed record PendingCode(byte[] Digest, DateTimeOffset Expires);
}

/// <summary>Where the host writes a one-time reset code: an owner-only file in its data directory.</summary>
public sealed record PasswordResetOptions(string CodeFilePath);

/// <summary>What a password change came to. <see cref="RecoveryKey"/> is shown once and never stored.</summary>
public sealed record PasswordChangeResult(bool Succeeded, string? RecoveryKey, PasswordError? Error)
{
    public static PasswordChangeResult Changed(string recoveryKey) => new(true, recoveryKey, null);

    public static PasswordChangeResult Failed(PasswordError error) => new(false, null, error);
}

/// <summary>Why a password was not changed.</summary>
public enum PasswordError
{
    TooShort,
    WrongPassword,
    WrongKey,
    WrongCode,
    TooManyAttempts,
    NotSaved,
    AlreadySet,
}

/// <summary>What a request for a one-time code came to.</summary>
public enum CodeRequestResult
{
    Issued,
    TooSoon,
    NoPassword,
}
