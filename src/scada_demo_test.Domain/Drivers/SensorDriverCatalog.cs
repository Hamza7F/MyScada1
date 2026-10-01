using System.Text.RegularExpressions;

namespace scada_demo_test.Domain.Drivers;

// Registry of every driver installed in this platform. The UI reads this catalog
// to render the "Sensor Library" dropdown; the polling worker uses it to resolve
// DriverKey -> parse logic; the schema engine uses it to build telemetry tables.
public static class SensorDriverCatalog
{
    private static readonly ISensorDriver[] AllDrivers =
    {
        new AosongAQ3485Driver(),
        new KaifengFlowmeterDriver(),
        new ElectromagneticFlowmeterDriver(),
        new VortexFlowmeterDriver(),
        new SelecPowerMeterDriver()
    };

    public static IReadOnlyList<ISensorDriver> Drivers => AllDrivers;

    public static ISensorDriver? GetByKey(string driverKey) =>
        AllDrivers.FirstOrDefault(d =>
            string.Equals(d.DriverKey, driverKey, StringComparison.OrdinalIgnoreCase));

    public static ISensorDriver RequireByKey(string driverKey) =>
        GetByKey(driverKey)
        ?? throw new InvalidOperationException($"No sensor driver registered with key '{driverKey}'.");
}

// Builds and validates the isolated per-sensor telemetry table name.
// Format: telemetry_sensor_{type}_{sensorIdShort}, uniquely bound to one Sensor.
public static class TelemetryTableNaming
{
    private static readonly Regex Allowed = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    public static string Build(string driverSimpleName, Guid sensorId)
    {
        var type = driverSimpleName.ToLowerInvariant();
        var suffix = sensorId.ToString("N")[..8];
        var name = $"telemetry_sensor_{type}_{suffix}";
        if (!IsSafeTableName(name))
        {
            throw new InvalidOperationException($"Generated telemetry table name '{name}' is not a safe PostgreSQL identifier.");
        }
        return name;
    }

    // Defense-in-depth: dynamic SQL is only ever executed against names produced
    // by this helper, which are locked to [a-z][a-z0-9_]*.
    public static bool IsSafeTableName(string? tableName) =>
        !string.IsNullOrWhiteSpace(tableName) && tableName.Length <= 128 && Allowed.IsMatch(tableName);
}