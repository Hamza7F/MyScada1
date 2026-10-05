using System.Collections.Concurrent;

namespace scada_demo_test.Infrastructure.Modbus;

/// <summary>
/// Coordinates bus access between the background polling hosted service and the
/// interactive Modbus bus scanner. Prevents TCP socket collisions on gateways
/// like USR-W610 that support only a single active TCP client.
/// </summary>
public static class ModbusScanCoordinator
{
    private static readonly ConcurrentDictionary<Guid, bool> _activeScans = new();

    public static bool IsScanning(Guid deviceId) => _activeScans.TryGetValue(deviceId, out var s) && s;

    public static void SetScanning(Guid deviceId, bool isScanning)
    {
        if (isScanning)
        {
            _activeScans[deviceId] = true;
        }
        else
        {
            _activeScans.TryRemove(deviceId, out _);
        }
    }
}
