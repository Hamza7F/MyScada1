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
            case 1: // Slave 1: Kaifeng IEMFL Electromagnetic Flowmeter (Water)
                {
                    if (functionCode != 0x03)
                    {
                        return new byte[] { (byte)(functionCode | 0x80), 0x02 }; // Illegal Data Address
                    }

                    float emFlowRate = (float)Math.Max(0.0, 14.2 + Math.Sin(elapsedSec * 0.1) * 2.5);
                    float emTotalizer = (float)(245.8 + elapsedSec * 0.015);
                    float emProof = 120.0f;

                    if (startRegister == 90 && quantity == 2) // Totalizer Window
                    {
                        registers[0] = GetFloatWordBigEndian(emTotalizer, highWord: true);
                        registers[1] = GetFloatWordBigEndian(emTotalizer, highWord: false);
                    }
                    else if (startRegister == 98 && quantity == 2) // Flow Rate Window
                    {
                        registers[0] = GetFloatWordBigEndian(emFlowRate, highWord: true);
                        registers[1] = GetFloatWordBigEndian(emFlowRate, highWord: false);
                    }
                    else if (startRegister == 92 && quantity == 2) // Proof Window
                    {
                        registers[0] = GetFloatWordBigEndian(emProof, highWord: true);
                        registers[1] = GetFloatWordBigEndian(emProof, highWord: false);
                    }
                    else
                    {
                        return new byte[] { (byte)(functionCode | 0x80), 0x02 }; // Illegal Data Address
                    }
                    break;
                }

            case 2: // Slave 2: DUAL COLLIDING INSTRUMENTS (Vortex Steam Flowmeter AND Selec RI-F200-C Power Meter)
                {
                    if (functionCode != 0x04)
                    {
                        return new byte[] { (byte)(functionCode | 0x80), 0x02 }; // Illegal Data Address
                    }

                    // --- Instrument A: Vortex Steam Flowmeter V880 (FC04 @1026/1032/1067) ---
                    if (startRegister == 1026 && quantity == 2) // Flow % Window
                    {
                        double flowM3H = Math.Max(0.0, 16.5 + Math.Sin(elapsedSec * 0.08) * 3.2);
                        float flowPct = (float)((flowM3H / 1000.0) * 100.0);
                        registers[0] = GetFloatWordBigEndian(flowPct, highWord: true);
                        registers[1] = GetFloatWordBigEndian(flowPct, highWord: false);
                    }
                    else if (startRegister == 1032 && quantity == 2) // Totalizer Window
                    {
                        float totalizer = (float)(725.0 + elapsedSec * 0.035);
                        registers[0] = GetFloatWordBigEndian(totalizer, highWord: true);
                        registers[1] = GetFloatWordBigEndian(totalizer, highWord: false);
                    }
                    else if (startRegister == 1067 && quantity == 2) // Proof Window
                    {
                        float tempProof = 32.5f;
                        registers[0] = GetFloatWordBigEndian(tempProof, highWord: true);
                        registers[1] = GetFloatWordBigEndian(tempProof, highWord: false);
                    }
                    // --- Instrument B: Selec RI-F200-C 3-Phase Power Meter (FC04 @42/58/64, low-word-first) ---
                    else if (startRegister == 42 && quantity == 2) // Active Power Window
                    {
                        float powerKw = (float)Math.Max(0.0, 41.5 + Math.Sin(elapsedSec * 0.12) * 5.5);
                        registers[0] = GetFloatWordLowWordFirst(powerKw, lowWord: true);
                        registers[1] = GetFloatWordLowWordFirst(powerKw, lowWord: false);
                    }
                    else if (startRegister == 58 && quantity == 2) // Energy Window
                    {
                        float energyKwh = (float)(20150.0 + elapsedSec * 0.08);
                        registers[0] = GetFloatWordLowWordFirst(energyKwh, lowWord: true);
                        registers[1] = GetFloatWordLowWordFirst(energyKwh, lowWord: false);
                    }
                    else if (startRegister == 64 && quantity <= 10) // Proof Window (Voltage & Current)
                    {
                        float voltage = (float)(230.5 + Math.Sin(elapsedSec * 0.15) * 1.8);
                        float current = (float)(18.2 + Math.Cos(elapsedSec * 0.15) * 1.2);
                        registers[0] = GetFloatWordLowWordFirst(voltage, lowWord: true);
                        if (quantity > 1) registers[1] = GetFloatWordLowWordFirst(voltage, lowWord: false);
                        if (quantity > 2) registers[2] = GetFloatWordLowWordFirst(current, lowWord: true);
                        if (quantity > 3) registers[3] = GetFloatWordLowWordFirst(current, lowWord: false);
                        for (int i = 4; i < quantity; i++) registers[i] = 0x0000;
                    }
                    else
                    {
                        return new byte[] { (byte)(functionCode | 0x80), 0x02 }; // Illegal Data Address
                    }
                    break;
                }

            case 3: // Slave 3: Aosong AQ3485 (Temperature & Humidity, FC03 @0)
                {
                    if (functionCode != 0x03 || startRegister != 0 || quantity != 2)
                    {
                        return new byte[] { (byte)(functionCode | 0x80), 0x02 }; // Illegal Data Address
                    }

                    double humidity = Math.Clamp(56.8 + Math.Sin(elapsedSec * 0.07) * 4.5, 20.0, 95.0);
                    double temp = Math.Clamp(29.1 + Math.Cos(elapsedSec * 0.06) * 2.8, 15.0, 45.0);

                    ushort rawHum = (ushort)Math.Round(humidity * 10.0);
                    short rawTemp = (short)Math.Round(temp * 10.0);

                    registers[0] = rawHum;
                    registers[1] = (ushort)rawTemp;
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
