using Gleanvolt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Infrastructure.Secrets;

/// <summary>
/// Which secret store this deployment gets, and why (issue #215).
///
/// <para><b>Explicit, never inferred from <c>OperatingSystem.IsWindows()</c> alone.</b> Windows is two
/// deployments, not one: the zip and winget installs, where DPAPI is the whole reason this exists, and
/// the Windows container, where it would seal secrets to an account that ceases to exist when the
/// container is recreated. Getting that wrong turns "my session expired" into "my session is gone
/// forever and the error is a crypto exception", so the choice is made here, in one function with no
/// I/O in it, and the reason it gives is what the startup log prints.</para>
/// </summary>
public static class SecretStoreSelection
{
    /// <summary>
    /// Set by the official .NET runtime images, both Linux and Nano Server. Read rather than probed
    /// for: a probe would have to guess at cgroups on one platform and at nothing at all on the other,
    /// and the images this repository's two Dockerfiles build from set it.
    /// </summary>
    private const string ContainerVariable = "DOTNET_RUNNING_IN_CONTAINER";

    /// <summary>Whether this process is running in a container, as the base image reports it.</summary>
    public static bool RunningInContainer() =>
        Environment.GetEnvironmentVariable(ContainerVariable) is "true" or "1";

    /// <summary>
    /// The store this deployment gets. Pure: the platform and the container are arguments, so the
    /// Windows decisions are testable on the Linux machine this is developed on.
    /// </summary>
    /// <param name="configured">What <c>Secrets:Store</c> asked for.</param>
    /// <param name="isWindows">Whether this is Windows.</param>
    /// <param name="inContainer">Whether this is a container.</param>
    /// <exception cref="InvalidOperationException">
    /// DPAPI was asked for off Windows, where it does not exist. Refused at startup rather than
    /// discovered at the first sign-in, when a person is waiting on an emailed code.
    /// </exception>
    public static SecretStoreChoice Choose(SecretStoreKind configured, bool isWindows, bool inContainer)
    {
        if (configured == SecretStoreKind.Dpapi && !isWindows)
        {
            throw new InvalidOperationException(
                "Secrets:Store is set to Dpapi (SECRETS__STORE), and the Windows Data Protection API "
                + "exists only on Windows. Use File, or leave it unset -- Auto picks the right one.");
        }

        if (configured == SecretStoreKind.File)
        {
            return new SecretStoreChoice(SecretStoreKind.File, "Secrets:Store is set to File", null);
        }

        if (configured == SecretStoreKind.Dpapi)
        {
            return new SecretStoreChoice(
                SecretStoreKind.Dpapi,
                "Secrets:Store is set to Dpapi",
                inContainer ? ContainerWarning : null);
        }

        if (!isWindows)
        {
            return new SecretStoreChoice(SecretStoreKind.File, "this is not Windows", null);
        }

        return inContainer
            // Honoured on Auto rather than merely warned about: an unrecoverable secret is worse than
            // a readable one, and the container's volume ACLs are what protects it either way.
            ? new SecretStoreChoice(
                SecretStoreKind.File,
                "this is a Windows container, where a DPAPI-sealed secret would not survive the "
                + "container being recreated",
                null)
            : new SecretStoreChoice(SecretStoreKind.Dpapi, "this is Windows", null);
    }

    /// <summary>The chosen store, built. The only function here that touches the file system or the OS.</summary>
    /// <param name="choice">What <see cref="Choose"/> decided.</param>
    /// <param name="directory">The data directory, absolute.</param>
    /// <param name="loggerFactory">For the store's own warnings, which name secrets and never their contents.</param>
    public static ISecretStore Create(SecretStoreChoice choice, string directory, ILoggerFactory? loggerFactory = null)
    {
        var files = new FileSecretStore(directory);

        if (choice.Kind != SecretStoreKind.Dpapi)
        {
            return files;
        }

        // Guarded rather than asserted: Choose refuses this combination, and the compiler wants the
        // platform check on the call rather than on the argument that produced it.
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("The DPAPI secret store was selected off Windows.");
        }

        return new DpapiSecretStore(files, loggerFactory?.CreateLogger<DpapiSecretStore>());
    }

    private const string ContainerWarning =
        "Secrets:Store is set to Dpapi inside a container. A Windows container runs under a built-in "
        + "account that does not survive the container being recreated, and a secret sealed to it is "
        + "then undecryptable rather than merely stale: the saved sign-in is gone for good. Set "
        + "SECRETS__STORE=File unless you know this container's profile is persistent.";
}

/// <summary>
/// Which store was chosen and why, so the startup log can say both.
/// </summary>
/// <param name="Kind">The store to build.</param>
/// <param name="Reason">Why, in a clause that reads after "because" — for the startup log.</param>
/// <param name="Warning">What is unwise about an explicit choice, or null when nothing is.</param>
public sealed record SecretStoreChoice(SecretStoreKind Kind, string Reason, string? Warning);
