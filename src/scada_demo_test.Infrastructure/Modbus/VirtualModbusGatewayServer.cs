using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace scada_demo_test.Infrastructure.Modbus;

/// <summary>
/// A high-fidelity, asynchronous Modbus TCP Virtual Gateway Server (USR-W610 simulation bridge).
/// Emulates real RS-485 slave instruments (Kaifeng Flowmeter, Vortex Flowmeter, Aosong Temp/Humidity,
/// and Selec 3-Phase Power Meter) using authentic Modbus TCP framing, register maps, and float codecs.
/// Enables 100% realistic end-to-end testing, scanning, polling, live charts, and alert evaluation
/// without requiring physical hardware to be switched on.
/// </summary>
public class VirtualModbusGatewayServer : BackgroundService
{
    private readonly ILogger<VirtualModbusGatewayServer> _logger;
    private readonly int _port;
    private readonly bool _enabled;
    private readonly DateTime _startTime = DateTime.UtcNow;

    public VirtualModbusGatewayServer(ILogger<VirtualModbusGatewayServer> logger, IConfiguration config)
    {
        _logger = logger;
        _port = int.TryParse(config["VirtualGateway:Port"], out var p) ? p : 5020;
        _enabled = config["VirtualGateway:Enabled"] != "false";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Virtual Modbus Gateway Server is disabled in configuration.");
            return;
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, _port);
            listener.Start();
            _logger.LogInformation("🚀 [VIRTUAL GATEWAY] Modbus TCP Server listening on 0.0.0.0:{Port} (USR-W610 RS-485 simulation active with Slaves 1..4)", _port);

            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Virtual Modbus Gateway Server encountered an error on port {Port}", _port);
        }
        finally
        {
            listener?.Stop();
            _logger.LogInformation("Virtual Modbus Gateway Server stopped.");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var headerBuf = new byte[7]; // MBAP Header: TransactionId(2), ProtocolId(2), Length(2), UnitId(1)
            var pduBuf = new byte[256];

            try
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    // Read 7-byte MBAP Header
                    if (!await ReadExactAsync(stream, headerBuf, 0, 7, ct))
                        break;

                    var transactionId = BinaryPrimitives.ReadUInt16BigEndian(headerBuf.AsSpan(0, 2));
                    var protocolId = BinaryPrimitives.ReadUInt16BigEndian(headerBuf.AsSpan(2, 2));
                    var length = BinaryPrimitives.ReadUInt16BigEndian(headerBuf.AsSpan(4, 2));
                    var unitId = headerBuf[6]; // Modbus Slave Address

                    if (protocolId != 0 || length < 2)
                        break;

                    var pduLength = length - 1;
                    if (pduLength > pduBuf.Length)
                        break;

                    if (!await ReadExactAsync(stream, pduBuf, 0, pduLength, ct))
                        break;

                    var functionCode = pduBuf[0];
                    if (pduLength < 5)
                        continue;

                    var startRegister = BinaryPrimitives.ReadUInt16BigEndian(pduBuf.AsSpan(1, 2));
                    var quantity = BinaryPrimitives.ReadUInt16BigEndian(pduBuf.AsSpan(3, 2));

                    var responsePayload = GenerateModbusResponse(unitId, functionCode, startRegister, quantity);
                    if (responsePayload is not null)
                    {
                        var responseMbap = new byte[7 + responsePayload.Length];
                        BinaryPrimitives.WriteUInt16BigEndian(responseMbap.AsSpan(0, 2), transactionId);
                        BinaryPrimitives.WriteUInt16BigEndian(responseMbap.AsSpan(2, 2), 0); // Protocol ID 0
                        BinaryPrimitives.WriteUInt16BigEndian(responseMbap.AsSpan(4, 2), (ushort)(1 + responsePayload.Length)); // Length = UnitId + Payload
                        responseMbap[6] = unitId;

                        Buffer.BlockCopy(responsePayload, 0, responseMbap, 7, responsePayload.Length);
                        await stream.WriteAsync(responseMbap, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogTrace("Virtual Modbus client connection closed: {Message}", ex.Message);
            }
        }
    }

    private byte[]? GenerateModbusResponse(byte unitId, byte functionCode, ushort startRegister, ushort quantity)
    {
        // Calculate simulation timeline
        var elapsedSec = (DateTime.UtcNow - _startTime).TotalSeconds;

        // Supported function codes: 0x03 (Read Holding) and 0x04 (Read Input)
        if (functionCode != 0x03 && functionCode != 0x04)
        {
            // Exception: Illegal Function
            return new byte[] { (byte)(functionCode | 0x80), 0x01 };
        }

        var registers = new ushort[quantity];

        switch (unitId)
        {
            case 2: // Slave 2: Aosong AQ3485 (Temperature & Humidity) ONLY
                {
                    if (functionCode == 0x03)
                    {
                        double humidity = Math.Clamp(58.0 + Math.Sin(elapsedSec * 0.08) * 6.0, 20.0, 95.0);
                        double temp = Math.Clamp(28.2 + Math.Cos(elapsedSec * 0.05) * 3.5, 15.0, 45.0);

                        ushort rawHum = (ushort)Math.Round(humidity * 10.0);
                        short rawTemp = (short)Math.Round(temp * 10.0);

                        for (int i = 0; i < quantity; i++)
                        {
                            int reg = startRegister + i;
                            if (reg == 0) registers[i] = rawHum;
                            else if (reg == 1) registers[i] = (ushort)rawTemp;
                            else registers[i] = 0x0000;
                        }
                    }
                    else
                    {
                        return new byte[] { (byte)(functionCode | 0x80), 0x01 };
                    }
                    break;
                }

            case 4: // Slave 4: DUAL COLLISION (Kaifeng Thermal Flowmeter on FC03 + Vortex Flowmeter on FC04)
                {
                    if (functionCode == 0x03)
                    {
                        // Kaifeng Thermal Flowmeter (FC03)
                        float flowRate = (float)Math.Max(0.0, 24.5 + Math.Sin(elapsedSec * 0.1) * 3.5);
                        float totalizer = (float)(1420.0 + elapsedSec * 0.05);

                        var allRegs = new ushort[24];
                        WriteFloatBigEndian(allRegs, 0, flowRate);
                        WriteFloatBigEndian(allRegs, 2, flowRate);
                        WriteFloatBigEndian(allRegs, 4, totalizer);
                        WriteFloatBigEndian(allRegs, 6, totalizer);
                        allRegs[8] = 0x0001;
                        allRegs[9] = 0x0064;

                        for (int i = 0; i < quantity; i++)
                        {
                            int targetReg = startRegister + i;
                            if (targetReg >= 0 && targetReg < allRegs.Length)
                                registers[i] = allRegs[targetReg];
                        }
                    }
                    else if (functionCode == 0x04)
                    {
                        // Vortex Flowmeter V880 (FC04)
                        float flowPct = (float)Math.Max(0.0, 2.5 + Math.Sin(elapsedSec * 0.12) * 0.4);
                        float totalizer = (float)(680.0 + elapsedSec * 0.025);

                        for (int i = 0; i < quantity; i++)
                        {
                            int reg = startRegister + i;
                            if (reg == 1026) registers[i] = GetFloatWordBigEndian(flowPct, highWord: true);
                            else if (reg == 1027) registers[i] = GetFloatWordBigEndian(flowPct, highWord: false);
                            else if (reg == 1032) registers[i] = GetFloatWordBigEndian(totalizer, highWord: true);
                            else if (reg == 1033) registers[i] = GetFloatWordBigEndian(totalizer, highWord: false);
                            else if (reg == 1067) registers[i] = 0x0012;
                            else if (reg == 1068) registers[i] = 0x0034;
                            else registers[i] = 0x0000;
                        }
                    }
                    else
                    {
                        return new byte[] { (byte)(functionCode | 0x80), 0x01 };
                    }
                    break;
                }

            case 6: // Slave 6: Vortex Flowmeter V880 (FC04 @1020..1070)
                {
                    if (functionCode == 0x04 && startRegister >= 1020 && startRegister <= 1070)
                    {
                        float flowPct = (float)Math.Max(0.0, 2.5 + Math.Sin(elapsedSec * 0.12) * 0.4);
                        float totalizer = (float)(680.0 + elapsedSec * 0.025);

                        for (int i = 0; i < quantity; i++)
                        {
                            int reg = startRegister + i;
                            if (reg == 1026) registers[i] = GetFloatWordBigEndian(flowPct, highWord: true);
                            else if (reg == 1027) registers[i] = GetFloatWordBigEndian(flowPct, highWord: false);
                            else if (reg == 1032) registers[i] = GetFloatWordBigEndian(totalizer, highWord: true);
                            else if (reg == 1033) registers[i] = GetFloatWordBigEndian(totalizer, highWord: false);
                            else if (reg == 1067) registers[i] = 0x0012;
                            else if (reg == 1068) registers[i] = 0x0034;
                            else registers[i] = 0x0000;
                        }
                    }
                    else
                    {
                        return new byte[] { (byte)(functionCode | 0x80), 0x02 }; // Illegal Data Address
                    }
                    break;
                }

            case 12: // Slave 12: Selec Power/Energy Meter (FC04 @40..100)
                {
                    if (functionCode == 0x04 && startRegister >= 40 && startRegister <= 100)
                    {
                        float powerKw = (float)Math.Max(0.0, 38.5 + Math.Sin(elapsedSec * 0.15) * 5.0);
                        float energyKwh = (float)(19840.0 + elapsedSec * 0.1);
                        float voltage = (float)(230.0 + Math.Sin(elapsedSec * 0.2) * 1.5);
                        float current = (float)(16.8 + Math.Cos(elapsedSec * 0.2) * 1.0);

                        for (int i = 0; i < quantity; i++)
                        {
                            int reg = startRegister + i;
                            if (reg == 42) registers[i] = GetFloatWordLowWordFirst(powerKw, lowWord: true);
                            else if (reg == 43) registers[i] = GetFloatWordLowWordFirst(powerKw, lowWord: false);
                            else if (reg == 58) registers[i] = GetFloatWordLowWordFirst(energyKwh, lowWord: true);
                            else if (reg == 59) registers[i] = GetFloatWordLowWordFirst(energyKwh, lowWord: false);
                            else if (reg == 64) registers[i] = GetFloatWordLowWordFirst(voltage, lowWord: true);
                            else if (reg == 65) registers[i] = GetFloatWordLowWordFirst(voltage, lowWord: false);
                            else if (reg == 66) registers[i] = GetFloatWordLowWordFirst(current, lowWord: true);
                            else if (reg == 67) registers[i] = GetFloatWordLowWordFirst(current, lowWord: false);
                            else registers[i] = 0x0000;
                        }
                    }
                    else
                    {
                        return new byte[] { (byte)(functionCode | 0x80), 0x02 }; // Illegal Data Address
                    }
                    break;
                }

            case 18: // Slave 18: Kaifeng Thermal Mass Flowmeter (FC03)
                {
                    if (functionCode == 0x03)
                    {
                        float flowRate = (float)Math.Max(0.0, 24.5 + Math.Sin(elapsedSec * 0.1) * 3.5);
                        float totalizer = (float)(1420.0 + elapsedSec * 0.05);

                        var allRegs = new ushort[24];
                        WriteFloatBigEndian(allRegs, 0, flowRate);
                        WriteFloatBigEndian(allRegs, 2, flowRate);
                        WriteFloatBigEndian(allRegs, 4, totalizer);
                        WriteFloatBigEndian(allRegs, 6, totalizer);
                        allRegs[8] = 0x0001;
                        allRegs[9] = 0x0064;

                        for (int i = 0; i < quantity; i++)
                        {
                            int targetReg = startRegister + i;
                            if (targetReg >= 0 && targetReg < allRegs.Length)
                                registers[i] = allRegs[targetReg];
                        }
                    }
                    else
                    {
                        return new byte[] { (byte)(functionCode | 0x80), 0x01 };
                    }
                    break;
                }

            default:
                // Nonexistent slave on the RS-485 bus
                return new byte[] { (byte)(functionCode | 0x80), 0x0B };
        }

        // Build Modbus PDU response: [FC (1 byte)] [ByteCount (1 byte)] [Data (Quantity * 2 bytes)]
        byte byteCount = (byte)(quantity * 2);
        var response = new byte[2 + byteCount];
        response[0] = functionCode;
        response[1] = byteCount;

        for (int i = 0; i < quantity; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + i * 2, 2), registers[i]);
        }

        return response;
    }

    private static void WriteFloatBigEndian(ushort[] regs, int startIdx, float value)
    {
        uint raw = BitConverter.SingleToUInt32Bits(value);
        regs[startIdx] = (ushort)(raw >> 16);
        regs[startIdx + 1] = (ushort)(raw & 0xFFFF);
    }

    private static ushort GetFloatWordBigEndian(float value, bool highWord)
    {
        uint raw = BitConverter.SingleToUInt32Bits(value);
        return highWord ? (ushort)(raw >> 16) : (ushort)(raw & 0xFFFF);
    }

    private static ushort GetFloatWordLowWordFirst(float value, bool lowWord)
    {
        uint raw = BitConverter.SingleToUInt32Bits(value);
        return lowWord ? (ushort)(raw & 0xFFFF) : (ushort)(raw >> 16);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int chunk = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read), ct);
            if (chunk == 0) return false;
            read += chunk;
        }
        return true;
    }
}
