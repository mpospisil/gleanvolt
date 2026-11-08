namespace Gleanvolt.Devices;

/// <summary>
/// The device answered with a Modbus exception response (issue #319): the link works, and the device
/// refused this one request. Every <see cref="IModbusClient"/> throws this, the real one and the replay one
/// alike, so a driver can tell "this family keeps nothing at that address" from "nothing answered" without
/// knowing which Modbus library is underneath.
/// </summary>
public sealed class ModbusDeviceException : Exception
{
    /// <summary>The function code is not supported by the device.</summary>
    public const byte IllegalFunction = 0x01;

    /// <summary>The device keeps nothing at (some of) the requested addresses.</summary>
    public const byte IllegalDataAddress = 0x02;

    /// <summary>A written value is not one the device accepts.</summary>
    public const byte IllegalDataValue = 0x03;

    /// <summary>The device failed while handling the request.</summary>
    public const byte ServerDeviceFailure = 0x04;

    /// <summary>A gateway (a data logger, an SDongle) has no path to the unit id.</summary>
    public const byte GatewayPathUnavailable = 0x0A;

    /// <summary>A gateway forwarded the request and the unit behind it did not answer.</summary>
    public const byte GatewayTargetFailedToRespond = 0x0B;

    public ModbusDeviceException(byte exceptionCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ExceptionCode = exceptionCode;
    }

    /// <summary>The Modbus exception code the device answered with.</summary>
    public byte ExceptionCode { get; }

    /// <summary>Whether the device said it keeps nothing at the address: a register this family or firmware lacks.</summary>
    public bool IsIllegalAddress => ExceptionCode == IllegalDataAddress;

    /// <summary>Whether a gateway answered for a unit it could not reach.</summary>
    public bool IsGateway => ExceptionCode is GatewayPathUnavailable or GatewayTargetFailedToRespond;
}
