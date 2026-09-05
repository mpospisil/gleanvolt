namespace Gleanvolt.Core.Models;

/// <summary>
/// Where a manufacturer-account sign-in has got to, and what to say about it (issue #170).
/// </summary>
/// <param name="Status">What the flow needs next.</param>
/// <param name="Message">A sentence for the page. Never carries a credential or a cookie.</param>
public sealed record VehicleSignInState(VehicleSignInStatus Status, string Message)
{
    public bool IsSignedIn => Status == VehicleSignInStatus.SignedIn;

    /// <summary>Whether a code box should be on screen.</summary>
    public bool WantsCode => Status == VehicleSignInStatus.CodeRequired;

    public static VehicleSignInState NotConfigured { get; } = new(
        VehicleSignInStatus.NotConfigured,
        "No manufacturer account is configured, so there is nothing to sign in to.");

    public static VehicleSignInState Unknown { get; } = new(
        VehicleSignInStatus.Unknown, "Not tried yet.");

    public static VehicleSignInState SignedIn(string message) => new(VehicleSignInStatus.SignedIn, message);

    public static VehicleSignInState CodeRequired(string message) =>
        new(VehicleSignInStatus.CodeRequired, message);

    public static VehicleSignInState Failed(string message) => new(VehicleSignInStatus.Failed, message);
}

/// <summary>The states a sign-in can be in. The kinds are the point: each needs a different thing.</summary>
public enum VehicleSignInStatus
{
    /// <summary>Nothing has been attempted.</summary>
    Unknown,

    /// <summary>No account configured; the page offers nothing.</summary>
    NotConfigured,

    /// <summary>Signed in; polling can run on this session.</summary>
    SignedIn,

    /// <summary>
    /// An emailed code is wanted. The owner reads it; nothing here retries until they have, and
    /// nothing loops — a login replayed at a real identity provider is how an account gets locked.
    /// </summary>
    CodeRequired,

    /// <summary>Refused, unreachable, or the flow lost its session part-way.</summary>
    Failed,
}
