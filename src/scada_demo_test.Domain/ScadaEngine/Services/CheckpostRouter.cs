using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using scada_demo_test.Domain.Drivers;
using ScadaEngine.Core.Models;

namespace ScadaEngine.Core.Services;

/// <summary>
/// Immutable metadata descriptor for each <see cref="DeviceProfileType"/> stored in
/// an O(1) in-memory <see cref="FrozenDictionary{TKey,TValue}"/> (Architectural Rule #2:
/// NO ROUTING DATABASE DEPENDENCY).
/// </summary>
public sealed record ProfileDescriptor(
    DeviceProfileType ProfileType,
    string DriverKey,
    string SimpleName,
    string ModelName,
    byte DefaultFunctionCode,
    ushort DefaultStartRegister,
    ushort DefaultRegisterQuantity,
    string UnitPrimary,
    string UnitSecondary,
    int DefaultPollIntervalSeconds);

/// <summary>
/// STEP 2: In-Memory Checkpost Router &amp; Signature Trial Engine.
/// Inspects incoming raw byte arrays (checking length, 16-bit register alignment,
/// IEEE-754 float finiteness, and physical bounds) and wraps verified payloads into a
/// strict <see cref="ModbusDevicePacket"/> identity envelope.
/// Unrecognized or corrupted frames are dropped immediately as RS-485 bus noise.
/// </summary>
public static class CheckpostRouter
{
    private static readonly FrozenDictionary<DeviceProfileType, ProfileDescriptor> ProfileTable =
        new Dictionary<DeviceProfileType, ProfileDescriptor>
        {
            [DeviceProfileType.AosongAQ3485] = new(
                DeviceProfileType.AosongAQ3485,
                DriverKey: "AOSONG_AQ3485",
                SimpleName: "aosong",
                ModelName: "Aosong AQ3485/Y (Temperature & Humidity)",
                DefaultFunctionCode: 0x03,
                DefaultStartRegister: 0x0000,
                DefaultRegisterQuantity: 2,
                UnitPrimary: "°C",
                UnitSecondary: "%RH",
                DefaultPollIntervalSeconds: 5),

            [DeviceProfileType.V880BRVortex] = new(
                DeviceProfileType.V880BRVortex,
                DriverKey: "VORTEX_FLOWMETER",
                SimpleName: "vortex",
                ModelName: "V880BR / LUGB Vortex Flowmeter",
                DefaultFunctionCode: 0x04,
                DefaultStartRegister: 1026,
                DefaultRegisterQuantity: 2,
                UnitPrimary: "m³/h",
                UnitSecondary: "m³",
                DefaultPollIntervalSeconds: 3),

            [DeviceProfileType.SelecPower] = new(
                DeviceProfileType.SelecPower,
                DriverKey: "SELEC_POWER_METER",
                SimpleName: "selec_power",
                ModelName: "Selec RI-F200-C 3-Phase Power/Energy Meter",
                DefaultFunctionCode: 0x04,
                DefaultStartRegister: 42,
                DefaultRegisterQuantity: 2,
                UnitPrimary: "kW",
                UnitSecondary: "kWh",
                DefaultPollIntervalSeconds: 5),

            [DeviceProfileType.Electromagnetic] = new(
                DeviceProfileType.Electromagnetic,
                DriverKey: "KAIFENG_EM_FLOWMETER",
                SimpleName: "kaifeng_em",
                ModelName: "Kaifeng IEMFL Electromagnetic Flowmeter (Water)",
                DefaultFunctionCode: 0x03,
                DefaultStartRegister: 90,
                DefaultRegisterQuantity: 2,
                UnitPrimary: "m³/h",
                UnitSecondary: "m³",
                DefaultPollIntervalSeconds: 3)
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, DeviceProfileType> DriverKeyToProfile =
        ProfileTable.Values
            .ToDictionary(p => p.DriverKey, p => p.ProfileType, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// O(1) lookup of profile metadata by <see cref="DeviceProfileType"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ProfileDescriptor? GetDescriptor(DeviceProfileType profile) =>
        ProfileTable.TryGetValue(profile, out var desc) ? desc : null;

    /// <summary>
    /// O(1) lookup of <see cref="DeviceProfileType"/> from an installed <c>DriverKey</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DeviceProfileType ResolveProfileByDriverKey(string? driverKey) =>
        !string.IsNullOrWhiteSpace(driverKey) && DriverKeyToProfile.TryGetValue(driverKey, out var profile)
            ? profile
            : DeviceProfileType.Unknown;

    /// <summary>
    /// Inspects a <see cref="RawScanResponse"/> from the Dual-Guard Scanner's RawBucket,
    /// executes byte-level Signature Trials, and returns a verified <see cref="ModbusDevicePacket"/>
    /// envelope. Returns <c>null</c> if the frame fails validation or is corrupted noise.
    /// </summary>
    public static ModbusDevicePacket? InspectAndTag(RawScanResponse? response)
    {
        if (response is null || !response.IsSuccess || response.Payload is null || response.Payload.Length == 0)
            return null;

        if (response.SlaveId is < 1 or > 255)
            return null;

        // Every valid Modbus register payload must have an even byte count (2 bytes per 16-bit register).
        if ((response.Payload.Length & 1) != 0)
            return null;

        var profile = RunSignatureTrial(
            response.Payload,
            response.HintedProfile,
            response.FunctionCode,
            response.StartRegister);

        if (profile == DeviceProfileType.Unknown || !ProfileTable.TryGetValue(profile, out var desc))
            return null;

        return new ModbusDevicePacket(
            slaveId: response.SlaveId,
            profileType: profile,
            driverKey: desc.DriverKey,
            modelName: desc.ModelName,
            rawPayload: response.Payload,
            functionCode: response.FunctionCode != 0 ? response.FunctionCode : desc.DefaultFunctionCode,
            startRegister: response.StartRegister != 0 ? response.StartRegister : desc.DefaultStartRegister,
            registerQuantity: response.RegisterQuantity != 0 ? response.RegisterQuantity : desc.DefaultRegisterQuantity,
            proofVerified: response.ProofServed,
            timestampUtc: DateTime.UtcNow);
    }

    /// <summary>
    /// Direct raw-byte overload for pure in-memory signature inspection &amp; envelope tagging.
    /// </summary>
    public static ModbusDevicePacket? InspectAndTag(
        byte slaveId,
        byte[] rawPayload,
        DeviceProfileType hintedProfile = DeviceProfileType.Unknown,
        byte functionCode = 0x03,
        ushort startRegister = 0,
        ushort registerQuantity = 0,
        bool proofVerified = true)
    {
        if (rawPayload is null || rawPayload.Length == 0 || (rawPayload.Length & 1) != 0)
            return null;

        if (slaveId is < 1 or > 255)
            return null;

        var profile = RunSignatureTrial(rawPayload, hintedProfile, functionCode, startRegister);
        if (profile == DeviceProfileType.Unknown || !ProfileTable.TryGetValue(profile, out var desc))
            return null;

        return new ModbusDevicePacket(
            slaveId: slaveId,
            profileType: profile,
            driverKey: desc.DriverKey,
            modelName: desc.ModelName,
            rawPayload: rawPayload,
            functionCode: functionCode != 0 ? functionCode : desc.DefaultFunctionCode,
            startRegister: startRegister != 0 ? startRegister : desc.DefaultStartRegister,
            registerQuantity: registerQuantity != 0 ? registerQuantity : desc.DefaultRegisterQuantity,
            proofVerified: proofVerified,
            timestampUtc: DateTime.UtcNow);
    }

    /// <summary>
    /// Performs deterministic, microsecond-latency Signature Trials on the raw byte buffer:
    ///   - Length == 4 bytes  -&gt; <see cref="DeviceProfileType.AosongAQ3485"/> (16-bit Ints)
    ///   - Length &gt;= 8 bytes -&gt; <see cref="DeviceProfileType.V880BRVortex"/> (32-bit IEEE-754 Floats)
    ///     or register-context disambiguated <see cref="DeviceProfileType.SelecPower"/> / <see cref="DeviceProfileType.Electromagnetic"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DeviceProfileType RunSignatureTrial(
        ReadOnlySpan<byte> payload,
        DeviceProfileType hintedProfile = DeviceProfileType.Unknown,
        byte functionCode = 0x03,
        ushort startRegister = 0)
    {
        if (payload.Length < 4 || (payload.Length & 1) != 0)
            return DeviceProfileType.Unknown;

        // 1. If the hardware scanner probed a specific register window and provided a profile hint,
        //    verify that the raw bytes satisfy that profile's strict structural trial.
        if (hintedProfile != DeviceProfileType.Unknown)
        {
            return VerifyProfileStructure(payload, hintedProfile)
                ? hintedProfile
                : DeviceProfileType.Unknown;
        }

        // 2. Register-window context disambiguation (when FunctionCode / StartRegister are known):
        if (functionCode == 0x04 && startRegister is 42 or 58 or 64)
        {
            return VerifyProfileStructure(payload, DeviceProfileType.SelecPower)
                ? DeviceProfileType.SelecPower
                : DeviceProfileType.Unknown;
        }
        if (functionCode == 0x04 && startRegister is 1026 or 1032 or 1067)
        {
            return VerifyProfileStructure(payload, DeviceProfileType.V880BRVortex)
                ? DeviceProfileType.V880BRVortex
                : DeviceProfileType.Unknown;
        }
        if (functionCode == 0x03 && startRegister is 90 or 92 or 98)
        {
            return VerifyProfileStructure(payload, DeviceProfileType.Electromagnetic)
                ? DeviceProfileType.Electromagnetic
                : DeviceProfileType.Unknown;
        }

        // 3. Pure Byte-Length & Boundary Signature Trial (Step 2 Specification):
        //    - Length == 4 bytes  -> Aosong AQ3485 (16-bit Ints: Humidity & Temperature)
        //    - Length >= 8 bytes  -> V880BR Vortex or Electromagnetic Flowmeter (32-bit IEEE-754 Floats)
        if (payload.Length == 4)
        {
            return VerifyAosong16BitSignature(payload)
                ? DeviceProfileType.AosongAQ3485
                : DeviceProfileType.Unknown;
        }

        if (payload.Length >= 8)
        {
            return VerifyIeee754HighWordFloatPair(payload)
                ? DeviceProfileType.V880BRVortex
                : DeviceProfileType.Unknown;
        }

        return DeviceProfileType.Unknown;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool VerifyProfileStructure(ReadOnlySpan<byte> payload, DeviceProfileType profile) =>
        profile switch
        {
            DeviceProfileType.AosongAQ3485 => VerifyAosong16BitSignature(payload),
            DeviceProfileType.V880BRVortex => VerifyIeee754HighWordFloatPair(payload),
            DeviceProfileType.Electromagnetic => VerifyIeee754HighWordFloatPair(payload),
            DeviceProfileType.SelecPower => VerifyIeee754LowWordFloatPair(payload),
            _ => false
        };

    /// <summary>
    /// Trial A: Aosong AQ3485/Y — 4 bytes (2 x 16-bit Big-Endian registers).
    /// Register[0] = Unsigned 16-bit Humidity * 10 (0.0 .. 100.0 %RH -&gt; raw 0 .. 1010)
    /// Register[1] = Signed 16-bit Temperature * 10 (-60.0 .. 150.0 °C -&gt; raw -600 .. 1500)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool VerifyAosong16BitSignature(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 4) return false;

        ushort rawHum = BinaryPrimitives.ReadUInt16BigEndian(payload[..2]);
        short rawTemp = BinaryPrimitives.ReadInt16BigEndian(payload.Slice(2, 2));

        double humRh = rawHum / 10.0;
        double tempC = rawTemp / 10.0;

        if (tempC == 0.0 && (humRh == 0.0 || humRh >= 99.0)) return false;
        if (humRh == 0.0 && tempC <= 5.0) return false;

        return humRh is >= 1.0 and <= 99.0 && tempC is >= -40.0 and <= 80.0;
    }



    /// <summary>
    /// Trial C: V880BR Vortex &amp; Kaifeng Electromagnetic — IEEE-754 High-Word-First float32
    /// (4 bytes single window or &gt;= 8 bytes dual-window combined payload).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool VerifyIeee754HighWordFloatPair(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return false;

        float primary = ReadFloat32BigEndian(payload[..4]);
        if (!float.IsFinite(primary) || Math.Abs(primary) > 1e7f)
            return false;

        if (payload.Length >= 8)
        {
            float secondary = ReadFloat32BigEndian(payload.Slice(4, 4));
            if (!float.IsFinite(secondary) || Math.Abs(secondary) > 1e12f)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Trial D: Selec RI-F200-C Power Meter — IEEE-754 Low-Word-First ("FLOAT REVERSE WORD") float32.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool VerifyIeee754LowWordFloatPair(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return false;

        float primary = ReadFloat32LowWordFirst(payload[..4]);
        if (!float.IsFinite(primary) || Math.Abs(primary) > 1e7f)
            return false;

        if (payload.Length >= 8)
        {
            float secondary = ReadFloat32LowWordFirst(payload.Slice(4, 4));
            if (!float.IsFinite(secondary) || Math.Abs(secondary) > 1e12f)
                return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ReadFloat32BigEndian(ReadOnlySpan<byte> bytes)
    {
        uint bits = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        float value = BitConverter.UInt32BitsToSingle(bits);
        return value == 0f ? 0f : value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ReadFloat32LowWordFirst(ReadOnlySpan<byte> bytes)
    {
        // Word 0 (low 16 bits) = bytes[0..1], Word 1 (high 16 bits) = bytes[2..3]
        uint bits = ((uint)bytes[2] << 24)
                  | ((uint)bytes[3] << 16)
                  | ((uint)bytes[0] << 8)
                  | bytes[1];
        float value = BitConverter.UInt32BitsToSingle(bits);
        return value == 0f ? 0f : value;
    }
}
