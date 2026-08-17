namespace Gleanvolt.Core.Enums;

/// <summary>
/// What the car reports it is doing with its own battery, normalised across sources.
///
/// <para>Every source spells this differently — the VW EU Data Act portal emits
/// <c>CHARGE_STATE_CHARGING_HV_BATTERY</c>, <c>volkswagen_connect</c> emits
/// <c>notReadyForCharging</c>, an OBD dongle emits raw CAN values. The mapping to these members is
/// done per car in Home Assistant, so this assembly only ever sees the vocabulary below and a second
/// vehicle costs no code here.</para>
///
/// <para><see cref="Unknown"/> is the default deliberately: it is the state of an absent or
/// unrecognised field, and every consumer has to handle it anyway.</para>
/// </summary>
public enum VehicleChargeState
{
    /// <summary>Not reported, or a value this vocabulary doesn't cover.</summary>
    Unknown = 0,

    /// <summary>Not charging — whether or not it is plugged in.</summary>
    Idle,

    /// <summary>Actively taking energy.</summary>
    Charging,

    /// <summary>Finished: the car reached its own target and stopped of its own accord.</summary>
    Complete,
}
