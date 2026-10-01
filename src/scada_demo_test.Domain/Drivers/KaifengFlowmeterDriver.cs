using scada_demo_test.Domain.Enums;

namespace scada_demo_test.Domain.Drivers;

// =====================================================================
// Driver 2 : Kaifeng Thermal Mass Flowmeter
// ---------------------------------------------------------------------
// Default Slave ID   : 0x01
// Function Code      : 0x03 (Read Holding Registers)
// Start Register     : 0x0001
// Register Quantity  : 0x000C (12 registers = 24 bytes)
// Serial framing     : 9600 baud, 8 data bits, No parity, 1 stop bit
// Conversion         : IEEE-754 32-bit float pairs (big-endian register order)
//                      Float32(Register[1], Register[2]) -> Flow Rate (m³/h)
//                      Float32(Register[3], Register[4]) -> Totalizer (m³)
// =====================================================================
public class KaifengFlowmeterDriver : ISensorDriver
{
    public string DriverKey => "KAIFENG_FLOWMETER";
    public string SimpleName => "kaifeng";
    public string DisplayName => "Kaifeng Thermal Mass Flowmeter";
    public string Description => "Thermal mass flowmeter, IEEE-754 32-bit floats for Instantaneous Flow Rate and Accumulated Totalizer.";

    public byte FunctionCode => 0x03;
    public ushort StartRegister => 0x0001;
    public ushort RegisterQuantity => 12;

    public string UnitPrimary => "m³/h";
    public string UnitSecondary => "m³";
    public string PrimaryColumnName => "InstantaneousFlowRate";
    public string SecondaryColumnName => "AccumulatedTotalizer";

    public int DefaultSlaveAddress => 0x01;
    public int DefaultPollIntervalSeconds => 3;

    public ParsedTelemetry ParseData(byte[] rawRegisters)
    {
        // Full IEEE-754 32-bit float pairs require 10 payload bytes (bulk of the
        // 24-byte reply from the 12-register read); never record a truncated frame
        // as a successful reading (silently zeroed totalizer = corrupted data).
        if (rawRegisters == null || rawRegisters.Length < 10)
        {
            return new ParsedTelemetry { Success = false, ErrorCode = "INVALID_PAYLOAD" };
        }

        // Register r occupies bytes [2*r .. 2*r+1]; a 32-bit float takes 2 registers.
        float flowRate = ModbusValueCodec.ToFloat32(rawRegisters, 2);
        float totalizer = ModbusValueCodec.ToFloat32(rawRegisters, 6);

        return new ParsedTelemetry
        {
            Success = true,
            PrimaryValue = Math.Round(flowRate, 2),
            SecondaryValue = Math.Round(totalizer, 2)
        };
    }
}