using System.Net.Sockets;

namespace scada_demo_test.Infrastructure.Modbus;

// Minimal, dependency-free Modbus TCP master client (RFC-standard Modbus/TCP with
// MBAP header). The gateways (Norvi ESP32 / USR-W610) forward the request out over
// RS-485 to the attached slave meters, so the platform only ever speaks TCP.
//
// One ModbusTcpSession is opened per device per polling cycle: every sensor attached
// to that gateway is read over the same connection, then the session is disposed.
public class ModbusTcpMaster
{
    public async Task<ModbusTcpSession> OpenAsync(string ipAddress, int port, int timeoutMs, CancellationToken ct)
    {
        var client = new TcpClient();

        var connectTask = client.ConnectAsync(ipAddress, port);
        var timeoutTask = Task.Delay(timeoutMs, ct);
        if (await Task.WhenAny(connectTask, timeoutTask) != connectTask)
        {
            client.Dispose();
            ct.ThrowIfCancellationRequested(); // report shutdown as cancellation, not timeout
            throw new ModbusConnectException($"Gateway {ipAddress}:{port} did not accept the connection within {timeoutMs} ms.");
        }

        try
        {
            await connectTask; // surfaces any immediate connect failure
        }
        catch
        {
            client.Dispose();
            throw;
        }

        client.ReceiveTimeout = timeoutMs;
        client.SendTimeout = timeoutMs;

        return new ModbusTcpSession(client, timeoutMs);
    }
}

// A TCP-level connect failure (the gateway refused or never accepted the socket
// within the connect budget). Distinct from ModbusException so the scanner can
// treat it as a gateway-wide fault rather than a silent slave address.
public class ModbusConnectException : Exception
{
    public ModbusConnectException(string message) : base(message) { }
    public ModbusConnectException(string message, Exception? inner) : base(message, inner) { }
}

// One persistent connection to a single gateway. Requests share the stream and a
// monotonic transaction-ID counter; receive latency is bounded by TimeoutMs.
public sealed class ModbusTcpSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly int _timeoutMs;
    private int _transactionCounter;
    private ushort _lastTxId;

    internal ModbusTcpSession(TcpClient client, int timeoutMs)
    {
        _client = client;
        _stream = client.GetStream();
        _timeoutMs = timeoutMs;
    }

    public Task<byte[]> ReadHoldingRegistersAsync(
        byte slaveId,
        ushort startRegister,
        ushort registerQuantity,
        CancellationToken ct,
        int? readTimeoutMs = null)
        => ReadRegistersAsync(0x03, slaveId, startRegister, registerQuantity, ct, readTimeoutMs);

    public Task<byte[]> ReadInputRegistersAsync(
        byte slaveId,
        ushort startRegister,
        ushort registerQuantity,
        CancellationToken ct,
        int? readTimeoutMs = null)
        => ReadRegistersAsync(0x04, slaveId, startRegister, registerQuantity, ct, readTimeoutMs);

    private async Task<byte[]> ReadRegistersAsync(
        byte responseFuncCode,
        byte slaveId,
        ushort startRegister,
        ushort registerQuantity,
        CancellationToken ct,
        int? readTimeoutMs = null)
    {
        // Effective read budget: transient override (bus scanner probe) or the
        // session default (polling worker). Enforced below with a real timer -
        // socket ReceiveTimeout does NOT bound async reads.
        var timeoutMs = readTimeoutMs is > 0 ? readTimeoutMs.Value : _timeoutMs;

        var request = BuildReadFrame(responseFuncCode, slaveId, startRegister, registerQuantity);
        await _stream.WriteAsync(request, ct);

        try
        {
            // Response header: MBAP(6) + unit(1) + function(1) + byteCount(1) = 9 bytes.
            var header = await ReadExactlyAsync(9, ct, timeoutMs);

            var responseTx = (ushort)((header[0] << 8) | header[1]);
            if (responseTx != _lastTxId)
            {
                throw new ModbusException($"Transaction ID mismatch: sent {_lastTxId:X4}, got {responseTx:X4}.", isProtocolError: true);
            }

            var responseUnit = header[6];
            var responseFunc = header[7];
            var byteCount = header[8];

            if (responseUnit != slaveId)
            {
                throw new ModbusException($"Unexpected unit ID {responseUnit}, expected {slaveId}.", isProtocolError: true);
            }
            if ((responseFunc & 0x80) != 0)
            {
                throw new ModbusException(
                    $"Slave {slaveId} returned Modbus exception code 0x{responseFunc & 0x0F:X2}.",
                    exceptionCode: (byte)(responseFunc & 0x0F));
            }
            if (responseFunc != responseFuncCode)
            {
                throw new ModbusException($"Unexpected function code 0x{responseFunc:X2}, expected 0x{responseFuncCode:X2}.", isProtocolError: true);
            }

            // Responses carry exactly registerQuantity*2 payload bytes; anything
            // else means a framing error or a partial reply and must not reach the driver.
            var expectedByteCount = registerQuantity * 2;
            if (byteCount != expectedByteCount)
            {
                throw new ModbusException($"Unexpected byte count {byteCount}, expected {expectedByteCount} for {registerQuantity} registers.", isProtocolError: true);
            }

            // Register data payload only (what the sensor driver parses).
            return await ReadExactlyAsync(byteCount, ct, timeoutMs);
        }
        catch (TimeoutException)
        {
            // A silent/busy slave owes us nothing: flush whatever did trickle in so
            // the NEXT read on this shared session starts on a clean stream (the fast
            // scanner continues without re-opening the socket). Asynchronous reads
            // that already completed mid-timeout leave the few extra bytes here.
            DrainPendingBytes();
            throw;
        }
    }

    private void DrainPendingBytes()
    {
        try
        {
            int guard = 0;
            while (_stream.DataAvailable && guard++ < 8)
            {
                _stream.ReadByte();
            }
        }
        catch
        {
            // Socket already broken; the caller decides whether to re-open.
        }
    }

    private byte[] BuildReadFrame(byte func, byte slaveId, ushort startRegister, ushort registerQuantity)
    {
        var tx = (ushort)Interlocked.Increment(ref _transactionCounter);
        _lastTxId = tx;

        var frame = new byte[12];
        frame[0] = (byte)(tx >> 8);      // Transaction ID (high)
        frame[1] = (byte)(tx & 0xFF);    // Transaction ID (low)
        frame[2] = 0x00;                 // Protocol ID
        frame[3] = 0x00;                 // Protocol ID
        frame[4] = 0x00;                 // Length (high)
        frame[5] = 0x06;                 // Length = 6 (unit + func + 2x address + 2x quantity)
        frame[6] = slaveId;              // Unit / Slave ID
        frame[7] = func;                 // Function code: 0x03 Read Holding / 0x04 Read Input
        frame[8] = (byte)(startRegister >> 8);
        frame[9] = (byte)(startRegister & 0xFF);
        frame[10] = (byte)(registerQuantity >> 8);
        frame[11] = (byte)(registerQuantity & 0xFF);
        return frame;
    }

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct, int timeoutMs)
    {
        var buffer = new byte[count];
        int offset = 0;

        // Linked CTS drives a REAL hard read deadline (CancelAfter), because async
        // reads ignore TcpClient.ReceiveTimeout. A silent serial bridge would
        // otherwise hang the probe forever.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        while (offset < count)
        {
            int read;
            try
            {
                read = await _stream.ReadAsync(buffer.AsMemory(offset, count - offset), cts.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Gateway did not respond within {timeoutMs} ms.", ex);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"Gateway did not respond within {timeoutMs} ms.", ex);
            }

            if (read == 0)
            {
                throw new IOException("Gateway closed the connection before the full response arrived.");
            }
            offset += read;
        }

        return buffer;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}

// Raised for any protocol-level Modbus failure (exception codes, bad unit/function,
// transaction mismatch) so the polling worker can tag the sensor OFFLINE gracefully.
public class ModbusException : Exception
{
    // Numeric Modbus exception code when the failure was an exception-frame reply
    // (e.g. 0x0B = gateway target device failed to respond). Null for framing errors.
    public byte? ExceptionCode { get; }

    // True when the reply was not a valid response to OUR request: unit ID mismatch,
    // transaction mismatch, wrong function code or wrong byte count. On a serial
    // bridge an empty address replays another device's cached frame, so this means
    // "this address does not serve this window" - the scanner maps it to WindowInvalid
    // and CONTINUES (it is NOT a bus-wide fault).
    public bool IsProtocolError { get; }

    public ModbusException(string message) : base(message) { }
    public ModbusException(string message, Exception? inner = null, byte? exceptionCode = null, bool isProtocolError = false)
        : base(message, inner) { ExceptionCode = exceptionCode; IsProtocolError = isProtocolError; }
}