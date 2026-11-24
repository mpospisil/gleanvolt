namespace Gleanvolt.Devices;

/// <summary>
/// Thrown by a charger driver that has nothing to talk to yet: for a charger behind a plugin, no plugin is
/// connected. It is an expected state, not a fault (a plugin connects some seconds after Gleanvolt starts,
/// and can be stopped by its owner), so the poll logs it as one line, without a stack trace.
/// </summary>
public sealed class ChargerNotConnectedException(string message) : InvalidOperationException(message);
