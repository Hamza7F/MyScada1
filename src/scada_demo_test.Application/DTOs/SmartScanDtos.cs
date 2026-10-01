namespace scada_demo_test.Application.DTOs;

// Contract for the Smart & Fast RS-485 bus scan (POST /api/devices/{id}/scan).
// The scanner is handed everything it needs (gateway IP/port, timeouts, the
// address range to probe and the list of slave addresses already registered)
// so it stays decoupled from EF and never touches the database - discovery
// happens purely over Modbus.
//
// Passes <= 0 means "auto" (the scanner applies its own tiering + pass count).
// DeepDuplicateCheck gates the two EXPENSIVE duplicate-detection reads (the
// winner's-own-window framing re-read and the foreign-window collision hunt);
// the cheap vote gate always runs. Quick scans set it false, "Find Sensors"
// leaves it true.
public record SmartScanRequest(
    Guid DeviceId,
    string DeviceName,
    string GatewayIp,
    int GatewayPort,
    int ConnectTimeoutMs,
    int ProbeTimeoutMs,
    IReadOnlyList<int> SkipSlaveAddresses,
    int StartAddress = 1,
    int EndAddress = 247,
    int Passes = 0,
    bool DeepDuplicateCheck = true);

// One possible driver match for a slave address, with the live values that
// driver's own identification window decoded and whether that driver could
// corroborate itself (its own proof block was served with data).
public record ScannedCandidateDto(
    string DriverKey,
    string DisplayName,
    ushort StartRegister,
    ushort RegisterQuantity,
    double? LivePrimary,
    double? LiveSecondary,
    bool ProofServed,
    bool BestGuess);

// One meter discovered during the scan. Detected "on the fly" from its register
// signature (start address + register map + plausibility of decoded values), with
// NO database row created - the UI turns a found meter into a Sensor only when
// the operator clicks "Map as Sensor".
public record ScannedMeterDto(
    int SlaveAddress,
    string? DriverKey,
    string? SimpleName,
    string? DisplayName,
    ushort StartRegister,
    ushort RegisterQuantity,
    string? UnitPrimary,
    string? UnitSecondary,
    int DefaultPollIntervalSeconds,
    string SuggestedName,
    double? LivePrimary,
    double? LiveSecondary,
    string Status, // "identified" | "unknown"
    bool IsAmbiguous = false,
    IReadOnlyList<ScannedCandidateDto>? Candidates = null);

// Two or more meters appear to share ONE slave address. The address's data is
// permanently ambiguous, so it is excluded from Found and reported here instead.
// DriverName is intentionally null (the hidden meter's type cannot be known for
// sure); MeterCount is the accurately counted number of colliding families.
public record DuplicateSlaveIdConflictDto(
    int SlaveAddress,
    int MeterCount,
    string? DriverName,
    string Message);

public record SmartScanResultDto(
    Guid DeviceId,
    string DeviceName,
    string? GatewayIp,
    int GatewayPort,
    int ProbeTimeoutMs,
    int SlavesScanned,
    int RespondingSlaves,
    int UnknownResponders,
    bool ConnectivityOk,
    string? ErrorCode,
    DateTime ScannedAtUtc,
    IReadOnlyList<ScannedMeterDto> Found,
    IReadOnlyList<int> SkippedSlaves,
    IReadOnlyList<int>? AmbiguousSlaves = null,
    IReadOnlyList<DuplicateSlaveIdConflictDto>? DuplicateIdConflicts = null);
