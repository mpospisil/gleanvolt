namespace Gleanvolt.Discovery;

/// <summary>
/// Spaces the Modbus requests to one host by <see cref="DiscoveryOptions.MinRequestInterval"/>, across
/// every port and unit id tried on it. Each attempt opens its own client, and a fresh client has no
/// memory of the last one's request, so the gap is kept here rather than by the client.
/// </summary>
internal sealed class HostPacer(TimeSpan interval, TimeProvider timeProvider)
{
    private long _lastRequest;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        if (_lastRequest != 0 && interval > TimeSpan.Zero)
        {
            var remaining = interval - timeProvider.GetElapsedTime(_lastRequest);

            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Sent() => _lastRequest = timeProvider.GetTimestamp();
}
