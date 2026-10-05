using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace scada_demo_test.VirtualBusTester;

public interface IVirtualSensor
{
    byte SlaveId { get; set; }
    string Name { get; }
    bool CanHandle(byte functionCode, ushort startRegister, ushort quantity);
    byte[]? HandleRequest(byte functionCode, ushort startRegister, ushort quantity);
}

public class VirtualAosongSensor : IVirtualSensor
{
    public byte SlaveId { get; set; }
    public string Name => $"Aosong AQ3485 (Slave {SlaveId})";
    public double TempC { get; set; }
    public double HumidityRh { get; set; }

    public VirtualAosongSensor(byte slaveId, double tempC = 28.5, double humidityRh = 55.2)
    {
        SlaveId = slaveId;
        TempC = tempC;
        HumidityRh = humidityRh;
    }

    public bool CanHandle(byte functionCode, ushort startRegister, ushort quantity)
    {
        return functionCode == 0x03 && startRegister == 0 && quantity == 2;
    }

    public byte[]? HandleRequest(byte functionCode, ushort startRegister, ushort quantity)
    {
        if (!CanHandle(functionCode, startRegister, quantity)) return null;

        // Big-endian Modbus registers:
        // Register 0 = Humidity * 10 (unsigned)
        // Register 1 = Temperature * 10 (signed)
        ushort rawHum = (ushort)Math.Clamp((int)Math.Round(HumidityRh * 10.0), 0, 65535);
        short rawTemp = (short)Math.Clamp((int)Math.Round(TempC * 10.0), -32768, 32767);

        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), rawHum);
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(2, 2), rawTemp);
        return payload;
    }
}

public class VirtualVortexSensor : IVirtualSensor
{
    public byte SlaveId { get; set; }
    public string Name => $"V880BR Vortex (Slave {SlaveId})";
    public double FlowM3H { get; set; }
    public double TotalizerM3 { get; set; }
    public float TempProof { get; set; }

    public VirtualVortexSensor(byte slaveId, double flowM3H = 14.5, double totalizerM3 = 680.0, float tempProof = 32.5f)
    {
        SlaveId = slaveId;
        FlowM3H = flowM3H;
        TotalizerM3 = totalizerM3;
        TempProof = tempProof;
    }

    public bool CanHandle(byte functionCode, ushort startRegister, ushort quantity)
    {
        return functionCode == 0x04 && (
            (startRegister == 1026 && quantity == 2) ||
            (startRegister == 1032 && quantity == 2) ||
            (startRegister == 1067 && quantity == 2));
    }

    public byte[]? HandleRequest(byte functionCode, ushort startRegister, ushort quantity)
    {
        if (!CanHandle(functionCode, startRegister, quantity)) return null;

        var payload = new byte[4];
        if (startRegister == 1026)
        {
            // Flow % of 1000 m3/h full scale
            float pct = (float)((FlowM3H / 1000.0) * 100.0);
            WriteFloat32HighWordFirst(payload, pct);
        }
        else if (startRegister == 1032)
        {
            WriteFloat32HighWordFirst(payload, (float)TotalizerM3);
        }
        else if (startRegister == 1067)
        {
            WriteFloat32HighWordFirst(payload, TempProof);
        }

        return payload;
    }

    private static void WriteFloat32HighWordFirst(byte[] buffer, float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, bits);
    }
}

public class VirtualKaifengEmSensor : IVirtualSensor
{
    public byte SlaveId { get; set; }
    public string Name => $"Kaifeng IEMFL Electromagnetic Flowmeter (Slave {SlaveId})";
    public double TotalizerM3 { get; set; }
    public double FlowRateM3H { get; set; }
    public float ProofValue { get; set; }

    public VirtualKaifengEmSensor(byte slaveId, double totalizerM3 = 245.2, double flowRateM3H = 9.8, float proofValue = 120.0f)
    {
        SlaveId = slaveId;
        TotalizerM3 = totalizerM3;
        FlowRateM3H = flowRateM3H;
        ProofValue = proofValue;
    }

    public bool CanHandle(byte functionCode, ushort startRegister, ushort quantity)
    {
        return functionCode == 0x03 && (
            (startRegister == 90 && quantity == 2) ||
            (startRegister == 98 && quantity == 2) ||
            (startRegister == 92 && quantity == 2));
    }

    public byte[]? HandleRequest(byte functionCode, ushort startRegister, ushort quantity)
    {
        if (!CanHandle(functionCode, startRegister, quantity)) return null;

        var payload = new byte[4];
        if (startRegister == 90)
        {
            WriteFloat32HighWordFirst(payload, (float)TotalizerM3);
        }
        else if (startRegister == 98)
        {
            WriteFloat32HighWordFirst(payload, (float)FlowRateM3H);
        }
        else if (startRegister == 92)
        {
            WriteFloat32HighWordFirst(payload, ProofValue);
        }

        return payload;
    }

    private static void WriteFloat32HighWordFirst(byte[] buffer, float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, bits);
    }
}

public class VirtualSelecPowerSensor : IVirtualSensor
{
    public byte SlaveId { get; set; }
    public string Name => $"Selec RI-F200-C Power Meter (Slave {SlaveId})";
    public double PowerKw { get; set; }
    public double EnergyKwh { get; set; }
    public double VoltageV { get; set; }
    public ushort CtPrimary { get; set; }
    public ushort CtSecondary { get; set; }

    public VirtualSelecPowerSensor(
        byte slaveId,
        double powerKw = 0.0,
        double energyKwh = 0.0,
        double voltageV = 230.0,
        ushort ctPrimary = 1000,
        ushort ctSecondary = 5)
    {
        SlaveId = slaveId;
        PowerKw = powerKw;
        EnergyKwh = energyKwh;
        VoltageV = voltageV;
        CtPrimary = ctPrimary;
        CtSecondary = ctSecondary;
    }

    public bool CanHandle(byte functionCode, ushort startRegister, ushort quantity)
    {
        if (functionCode == 0x04)
        {
            return (startRegister == 42 && quantity == 2) ||
                   (startRegister == 58 && quantity == 2) ||
                   (startRegister == 64 && quantity <= 10);
        }
        if (functionCode == 0x03)
        {
            return startRegister == 0 && quantity == 2;
        }
        return false;
    }

    public byte[]? HandleRequest(byte functionCode, ushort startRegister, ushort quantity)
    {
        if (!CanHandle(functionCode, startRegister, quantity)) return null;

        if (functionCode == 0x04)
        {
            var payload = new byte[quantity * 2];
            if (startRegister == 42)
            {
                WriteFloat32LowWordFirst(payload, (float)PowerKw);
            }
            else if (startRegister == 58)
            {
                WriteFloat32LowWordFirst(payload, (float)EnergyKwh);
            }
            else if (startRegister == 64)
            {
                WriteFloat32LowWordFirst(payload, (float)VoltageV);
                // If more registers requested, fill with nominal 3-phase values
                for (int i = 4; i < payload.Length; i += 4)
                {
                    WriteFloat32LowWordFirst(payload.AsSpan(i, 4), 230.0f);
                }
            }
            return payload;
        }

        if (functionCode == 0x03 && startRegister == 0 && quantity == 2)
        {
            // Selec config holding registers: CT Primary, CT Secondary
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), CtPrimary);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2, 2), CtSecondary);
            return payload;
        }

        return null;
    }

    private static void WriteFloat32LowWordFirst(Span<byte> buffer, float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        ushort lowWord = (ushort)(bits & 0xFFFF);
        ushort highWord = (ushort)(bits >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[..2], lowWord);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), highWord);
    }
}

public sealed class VirtualModbusGatewayServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IVirtualSensor> _sensors = new();
    private readonly object _lock = new();
    private Task? _listenTask;

    public int Port { get; }

    public VirtualModbusGatewayServer(int port = 15502)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void SetSensors(IEnumerable<IVirtualSensor> sensors)
    {
        lock (_lock)
        {
            _sensors.Clear();
            _sensors.AddRange(sensors);
        }
    }

    public void Start()
    {
        _listener.Start();
        _listenTask = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var header = new byte[6];
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header, 0, 6, ct)) break;

                ushort transId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
                ushort protoId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
                ushort length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));

                if (length == 0) continue;

                var pdu = new byte[length];
                if (!await ReadExactAsync(stream, pdu, 0, length, ct)) break;

                byte slaveId = pdu[0];
                byte functionCode = pdu[1];
                ushort startRegister = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(2, 2));
                ushort quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(4, 2));

                byte[]? responsePdu = ProcessModbusRequest(slaveId, functionCode, startRegister, quantity);
                if (responsePdu is null)
                {
                    // Target device nonexistent on the bus -> Modbus Exception 0x0B
                    responsePdu = new byte[] { (byte)(functionCode | 0x80), 0x0B };
                }

                // Send Modbus TCP MBAP Response:
                // [TransId: 2] [ProtoId: 2] [Length: 2] [SlaveId: 1] [ResponsePdu: N]
                ushort respLen = (ushort)(1 + responsePdu.Length);
                var mbap = new byte[7 + responsePdu.Length];
                BinaryPrimitives.WriteUInt16BigEndian(mbap.AsSpan(0, 2), transId);
                BinaryPrimitives.WriteUInt16BigEndian(mbap.AsSpan(2, 2), protoId);
                BinaryPrimitives.WriteUInt16BigEndian(mbap.AsSpan(4, 2), respLen);
                mbap[6] = slaveId;
                Array.Copy(responsePdu, 0, mbap, 7, responsePdu.Length);

                await stream.WriteAsync(mbap.AsMemory(), ct);
                await stream.FlushAsync(ct);
            }
        }
    }

    private byte[]? ProcessModbusRequest(byte slaveId, byte functionCode, ushort startRegister, ushort quantity)
    {
        List<IVirtualSensor> matchingSensors;
        lock (_lock)
        {
            matchingSensors = _sensors.Where(s => s.SlaveId == slaveId).ToList();
        }

        if (matchingSensors.Count == 0)
        {
            return null; // Not present on bus -> Exception 0x0B
        }

        // Find sensors on this slave ID that can handle this specific request
        var capable = matchingSensors.Where(s => s.CanHandle(functionCode, startRegister, quantity)).ToList();

        if (capable.Count == 0)
        {
            // Device exists on this Slave ID, but does not support this register or function code
            return new byte[] { (byte)(functionCode | 0x80), 0x02 }; // Illegal Data Address
        }

        // Exactly one sensor handled the request (or first sensor on shared bus)
        byte[]? dataPayload = capable[0].HandleRequest(functionCode, startRegister, quantity);
        if (dataPayload is null)
        {
            return new byte[] { (byte)(functionCode | 0x80), 0x02 };
        }

        // Return Modbus Data Response: [FunctionCode: 1] [ByteCount: 1] [Data: N]
        var pdu = new byte[2 + dataPayload.Length];
        pdu[0] = functionCode;
        pdu[1] = (byte)dataPayload.Length;
        Array.Copy(dataPayload, 0, pdu, 2, dataPayload.Length);
        return pdu;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total), ct);
            if (read == 0) return false;
            total += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_listenTask is not null)
        {
            try { await _listenTask; } catch { }
        }
        _cts.Dispose();
    }
}
