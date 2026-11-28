namespace Gleanvolt.Chargers;

/// <summary>A device's address on the LAN: a host, and a port when it is not the device's usual one.</summary>
/// <param name="Host">A host name or an IPv4 address.</param>
/// <param name="Port">The port.</param>
public sealed record DeviceAddress(string Host, int Port)
{
    /// <summary>
    /// <paramref name="text"/> as an address, <paramref name="defaultPort"/> when it names none: <c>192.168.1.6</c>,
    /// <c>charger.local:8080</c>, or an <c>http://</c> address with nothing after the host. Null when it is none of these.
    /// </summary>
    public static DeviceAddress? Parse(string? text, int defaultPort)
    {
        var trimmed = text?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed + "/", UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Host.Length == 0 || uri.UserInfo.Length > 0)
        {
            return null;
        }

        return new DeviceAddress(uri.Host, uri.IsDefaultPort && !trimmed.EndsWith(":80", StringComparison.Ordinal) ? defaultPort : uri.Port);
    }

    /// <summary>The address as the owner writes it.</summary>
    public override string ToString() => $"{Host}:{Port}";

    /// <summary>The address as an HTTP base address.</summary>
    public Uri ToHttpBase() => new($"http://{Host}:{Port}/");
}
