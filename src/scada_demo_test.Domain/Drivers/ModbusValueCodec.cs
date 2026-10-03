namespace scada_demo_test.Domain.Drivers;

/// <summary>
/// IEEE-754 float32 register decoding helpers shared by the flowmeter and power
/// meter drivers. Folds exact negative zero (-0.0) to positive zero (0.0) so
/// "-0" never leaks into UI cards, charts, or CSV/PDF exports.
/// </summary>
public static class ModbusValueCodec
{
    public static float ToFloat32(byte[] raw, int byteOffset, bool highWordFirst = true)
    {
        return highWordFirst
            ? (float)ReadFloat32HighWordFirst(raw, byteOffset)
            : (float)ReadFloat32LowWordFirst(raw, byteOffset);
    }

    public static double ReadFloat32HighWordFirst(byte[] raw, int byteOffset)
    {
        if (raw is null || raw.Length < byteOffset + 4) return 0d;

        // Register N = high word (raw[offset], raw[offset+1]),
        // Register N+1 = low word (raw[offset+2], raw[offset+3]) -> big-endian IEEE-754
        Span<byte> bytes = stackalloc byte[4]
        {
            raw[byteOffset + 3],
            raw[byteOffset + 2],
            raw[byteOffset + 1],
            raw[byteOffset + 0]
        };

        var value = (double)BitConverter.ToSingle(bytes);
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0d;
        return value == 0d ? 0d : value;
    }

    public static double ReadFloat32LowWordFirst(byte[] raw, int byteOffset)
    {
        if (raw is null || raw.Length < byteOffset + 4) return 0d;

        // Register N = low word (raw[offset], raw[offset+1]),
        // Register N+1 = high word (raw[offset+2], raw[offset+3]) -> float reverse word
        Span<byte> bytes = stackalloc byte[4]
        {
            raw[byteOffset + 1],
            raw[byteOffset + 0],
            raw[byteOffset + 3],
            raw[byteOffset + 2]
        };

        var value = (double)BitConverter.ToSingle(bytes);
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0d;
        return value == 0d ? 0d : value;
    }
}
