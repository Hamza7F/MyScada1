using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using scada_demo_test.Application.DTOs;
using scada_demo_test.Application.Interfaces;
using scada_demo_test.Domain.Drivers;
using ScadaEngine.Core.Drivers;
using ScadaEngine.Core.Models;
using ScadaEngine.Core.Services;

namespace scada_demo_test.Infrastructure.Modbus;

// =====================================================================
// Smart & Fast RS-485 Bus Scanner + Dual-Guard & Checkpost Pipeline
// ---------------------------------------------------------------------
// Sweeps Modbus slave addresses over the USR-W610 gateway and feeds all
// raw responses through the 3-Bucket Dual-Guard Architecture:
//   Bucket 1 (Guard 1 - DB Existence Guard):
//     AlreadyInSystemDevices / AlreadyInSystemSlaves ("Already added in system. Change hardware ID")
//   Bucket 2 (Guard 2 - Current Scan Duplicate Guard):
//     BlockedScanDuplicates / DuplicateIdConflicts ("Bus conflict: Same ID responded multiple times")
//   Bucket 3 (CheckpostRouter -> SensorDriverDispatcher "Ghar" -> Found Box):
//     UniqueFoundDevices / Found (Verified ModbusDevicePacket envelopes only)
// =====================================================================
public sealed class ModbusScanner : ISmartScanService
{
    private const byte MinAddress = 1;
    private const byte MaxAddress = 255;

    // Quick = slaves 1..20 (where plant meters actually live). Full = the whole
    // bus; the tier boundary keeps the two tiers disjoint so EVERY address is
    // probed exactly once and "however many are connected, all are found" holds.
    private const int QuickScanEndAddress = 20;
    private const int ScanPassesQuick = 1;
    private const int ScanPassesFull = 1;
    private const int AddressRefineAttempts = 1;

    // One bad address must never abort the sweep; only a sustained run of
    // socket-level failures (a genuinely dead bridge) ends it.
    private const int UnreachableFailureStreak = 12;

    // A family qualifies as "colliding" only with this many strong votes, and a
    // duplicate is declared only when >= 2 distinct families qualify.
    private const int ConflictVoteThreshold = 2;

    // Foreign-window collision hunt (deep mode only): a foreign block that is
    // answered a FEW PERCENT of the time means another device is racing this
    // address for the same request. 0% (nobody serves it) and ~100% (the
    // address's own meter or a zero-filler owns it) are both NOT witnesses.
    private const int ForeignCollisionProbeBudget = 300;
    private const int ForeignCollisionReadsPerConnection = 40;
    private const int ForeignCollisionWindowJumpMs = 20;
    private const int ForeignCollisionReadTimeoutMs = 120;
    private const int ForeignCollisionEarlyWitnessSamples = 25;
    private const int ForeignCollisionGiveUpSamples = 40;

    // A driver about to be thrown away by the degeneracy guard gets its proof
    // block re-read this many times before rejection - a transport hiccup must
    // not lose a genuine match.
    private const int DegeneracyProofAttempts = 8;
    private const int InterWindowDelayMs = 60;
    private const int MaxProbeTimeoutMs = 1000;

    private readonly IReadOnlyList<ISensorDriver> _drivers = SensorDriverCatalog.Drivers;
    private readonly ILogger<ModbusScanner>? _logger;

    public ModbusScanner(ILogger<ModbusScanner>? logger = null) => _logger = logger;

    // =================================================================
    // Public entry point
    // =================================================================
    public async Task<SmartScanResultDto> ScanAsync(SmartScanRequest request, CancellationToken ct = default)
    {
        var probeTimeout = request.ProbeTimeoutMs <= 0 ? 200 : Math.Clamp(request.ProbeTimeoutMs, 10, MaxProbeTimeoutMs);
        var connectMs = Math.Clamp(request.ConnectTimeoutMs, 100, 10000);

        // STEP 3 Pre-Scan Initialization: Load existing DB-registered Slave IDs into O(1) HashSet<byte>
        var regMeta = request.RegisteredSlaveNames?.ToDictionary(
            kv => kv.Key,
            kv => ((string?)kv.Value, (string?)null));
        var dualGuardService = new ModbusScannerService(request.SkipSlaveAddresses, regMeta);

        var reqStart = Math.Clamp(request.StartAddress, MinAddress, MaxAddress);
        var reqEnd = Math.Clamp(
            request.EndAddress < request.StartAddress ? request.StartAddress : request.EndAddress,
            MinAddress, MaxAddress);
        var quickEnd = Math.Min(Math.Clamp((int)QuickScanEndAddress, (int)MinAddress, (int)MaxAddress), reqEnd);
        connectMs = Math.Min(connectMs, 1500);

        // Passes <= 0 => automatic "Find Sensors" sweep (1..20 then 21..247).
        // Passes > 0 => Quick: 1..20 only.
        var tiers = new List<(int Start, int End, int Passes)>();
        if (request.Passes <= 0)
        {
            tiers.Add((reqStart, quickEnd, ScanPassesFull));
            if (reqEnd > quickEnd) tiers.Add((quickEnd + 1, reqEnd, ScanPassesFull));
        }
        else
        {
            tiers.Add((reqStart, quickEnd, Math.Max(1, request.Passes)));
        }

        var states = new Dictionary<int, AddressState>();
        var skipped = new List<int>();
        var incomingRawResponses = new List<RawScanResponse>();
        int totalProbed = 0;
        bool connectivityOk = true;
        string? errorCode = null;
        int failureStreak = 0;
        bool abort = false;

        var master = new ModbusTcpMaster();
        try
        {
            foreach (var (tierStart, tierEnd, passes) in tiers)
            {
                for (int pass = 0; pass < passes && !abort; pass++)
                {
                    for (int slave = tierStart; slave <= tierEnd && !abort; slave++)
                    {
                        // =========================================================
                        // CHECKPOINT 1 (GUARD 1: DB / System Existence Check)
                        // =========================================================
                        if (dualGuardService.IsRegisteredInDatabase((byte)slave))
                        {
                            if (!skipped.Contains(slave))
                            {
                                skipped.Add(slave);
                                incomingRawResponses.Add(new RawScanResponse
                                {
                                    SlaveId = (byte)slave,
                                    Payload = new byte[] { 0x00, 0x00, 0x00, 0x00 },
                                    IsSuccess = true
                                });
                            }
                            continue;
                        }

                        totalProbed++;
                        SlaveProbe probe;
                        try
                        {
                            probe = await ProbeSlaveAsync(master, request, connectMs, (byte)slave, probeTimeout, ct);
                        }
                        catch (Exception ex) when (ex is TimeoutException or IOException or SocketException or ObjectDisposedException)
                        {
                            if (++failureStreak >= UnreachableFailureStreak)
                            {
                                connectivityOk = false;
                                errorCode = "GATEWAY_UNREACHABLE";
                                abort = true;
                            }
                            continue;
                        }

                        if (probe.ConnectionUnsafe)
                        {
                            if (++failureStreak >= UnreachableFailureStreak)
                            {
                                connectivityOk = false;
                                errorCode = "GATEWAY_UNREACHABLE";
                                abort = true;
                            }
                            continue;
                        }

                        failureStreak = 0;
                        MergeOutcome(states, slave, probe);
                    }
                }
            }

            // Targeted refine of ONLY responding addresses for accuracy without stressing the USR-W610 bridge.
            if (!abort && AddressRefineAttempts > 0)
            {
                var responders = states.Where(kv => kv.Value.Responded).Select(kv => kv.Key).ToList();
                for (int attempt = 0; attempt < AddressRefineAttempts && !abort; attempt++)
                {
                    foreach (var slave in responders)
                    {
                        if (!states.TryGetValue(slave, out var st) || !st.Responded) continue;
                        try
                        {
                            var probe = await ProbeSlaveAsync(master, request, connectMs, (byte)slave, probeTimeout, ct);
                            if (probe.ConnectionUnsafe) continue;
                            MergeOutcome(states, slave, probe);
                        }
                        catch (Exception ex) when (ex is TimeoutException or IOException or SocketException or ObjectDisposedException)
                        {
                            // best-effort refine pass
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            connectivityOk = false;
            errorCode = "SCAN_CANCELLED";
        }

        // =====================================================================
        // Feed non-DB responding addresses into the Dual-Guard RawBucket
        // & CheckpostRouter -> SensorDriverDispatcher ("Ghar") -> Found Box
        // =====================================================================
        var unknownItems = new List<ScannedMeterDto>();
        var ambiguousSlaves = new List<int>();
        var ambiguityMap = new Dictionary<byte, (bool IsAmbiguous, List<ScannedCandidateDto>? Candidates)>();
        int responding = 0;
        int unknown = 0;

        foreach (var (slave, st) in states.OrderBy(kv => kv.Key))
        {
            if (!st.Responded) continue;
            responding++;

            if (st.Candidates.Count == 0)
            {
                unknown++;
                _logger?.LogInformation("scan slave {Slave}: responded but NO candidate accepted (unknown responder)", slave);
                unknownItems.Add(new ScannedMeterDto(
                    slave, null, null, "Unknown responder", 0, 0, null, null, 0,
                    $"Unidentified device (Slave {slave})", null, null, "unknown"));
                continue;
            }

            var validCandidates = st.Candidates
                .Where(c => DriverOf(c.Dto.DriverKey) is not null)
                .OrderBy(c => c.Rank)
                .ToList();

            if (validCandidates.Count == 0)
            {
                unknown++;
                _logger?.LogInformation("scan slave {Slave}: responded but NO candidate accepted (unknown responder)", slave);
                unknownItems.Add(new ScannedMeterDto(
                    slave, null, null, "Unknown responder", 0, 0, null, null, 0,
                    $"Unidentified device (Slave {slave})", null, null, "unknown"));
                continue;
            }

            foreach (var cand in validCandidates)
            {
                var drv = DriverOf(cand.Dto.DriverKey)!;
                incomingRawResponses.Add(new RawScanResponse
                {
                    SlaveId = (byte)slave,
                    Payload = cand.RawPayload.Length > 0 ? cand.RawPayload : new byte[] { 0, 0, 0, 0 },
                    IsSuccess = true,
                    HintedProfile = CheckpostRouter.ResolveProfileByDriverKey(drv.DriverKey),
                    FunctionCode = drv.FunctionCode,
                    StartRegister = cand.Dto.StartRegister,
                    RegisterQuantity = cand.Dto.RegisterQuantity,
                    ProofServed = cand.Dto.ProofServed,
                    ModelName = drv.DisplayName
                });
            }
        }

        // =====================================================================
        // Execute Dual-Guard Pipeline + CheckpostRouter + DriverDispatcher
        // =====================================================================
        ScanResultSummary summary = dualGuardService.ExecuteDualGuardPipeline(
            incomingRawResponses,
            includeAllRegisteredInBucket1: true);

        var found = new List<ScannedMeterDto>(summary.UniqueFoundDevices.Count + unknownItems.Count);
        foreach (var packet in summary.UniqueFoundDevices)
        {
            var desc = CheckpostRouter.GetDescriptor(packet.ProfileType);
            summary.DispatchedCalculations.TryGetValue(packet.SlaveId, out var calc);
            ambiguityMap.TryGetValue(packet.SlaveId, out var ambInfo);

            // Look up the candidate's live primary/secondary (in case multi-window secondary was merged)
            double? livePri = calc?.PrimaryValue;
            double? liveSec = calc?.SecondaryValue;
            if (states.TryGetValue(packet.SlaveId, out var addrState) && addrState.Candidates.Count > 0)
            {
                var topDto = addrState.Candidates.OrderBy(c => c.Rank).First().Dto;
                livePri = topDto.LivePrimary ?? livePri;
                liveSec = topDto.LiveSecondary ?? liveSec;
            }

            found.Add(new ScannedMeterDto(
                SlaveAddress: packet.SlaveId,
                DriverKey: packet.DriverKey,
                SimpleName: desc?.SimpleName ?? packet.DriverKey.ToLowerInvariant(),
                DisplayName: packet.ModelName,
                StartRegister: packet.StartRegister,
                RegisterQuantity: packet.RegisterQuantity,
                UnitPrimary: desc?.UnitPrimary ?? calc?.PrimaryUnit,
                UnitSecondary: desc?.UnitSecondary ?? calc?.SecondaryUnit,
                DefaultPollIntervalSeconds: desc?.DefaultPollIntervalSeconds ?? 5,
                SuggestedName: $"{packet.ModelName} (Slave {packet.SlaveId})",
                LivePrimary: livePri,
                LiveSecondary: liveSec,
                Status: "identified",
                IsAmbiguous: ambInfo.IsAmbiguous,
                Candidates: ambInfo.Candidates,
                ProfileType: packet.ProfileType.ToString(),
                RawHexPayload: packet.RawHex));
        }

        // Append any raw unknown responders so diagnostics remain complete
        found.AddRange(unknownItems);

        var conflicts = summary.BlockedDuplicateDetails
            .Select(b => new DuplicateSlaveIdConflictDto(
                b.SlaveId,
                b.ResponseCount,
                null,
                b.Message,
                b.CollidingDeviceNames))
            .ToList();

        var alreadyInSystemList = summary.AlreadyInSystemDetails
            .Select(a => new AlreadyInSystemSlaveDto(
                a.SlaveId,
                a.ExistingSensorName,
                a.Message))
            .ToList();

        return new SmartScanResultDto(
            request.DeviceId,
            request.DeviceName,
            request.GatewayIp,
            request.GatewayPort,
            probeTimeout,
            totalProbed,
            responding,
            unknown + summary.DroppedNoiseFrames,
            connectivityOk,
            errorCode,
            DateTime.UtcNow,
            found,
            summary.AlreadyInSystemDevices.Select(b => (int)b).ToList(),
            ambiguousSlaves,
            conflicts,
            alreadyInSystemList);
    }

    // =================================================================
    // Per-address probing
    // =================================================================
    private async Task<SlaveProbe> ProbeSlaveAsync(
        ModbusTcpMaster master,
        SmartScanRequest req,
        int connectMs,
        byte slave,
        int probeTimeout,
        CancellationToken ct)
    {
        var result = new SlaveProbe();

        // Half-duplex RS-485 serial safety: do NOT send concurrent requests over the gateway.
        // Sequential probe: try FC03 @ 0 first. If responding, presence is already proven!
        var (k3, _) = await ReadBlockAsync(master, req, connectMs, slave, new SensorReadWindow(0x03, 0, 2), probeTimeout, ct);
        if (k3 == ReadKind.Unsafe || k3 == ReadKind.ConnectFailed) { result.ConnectionUnsafe = true; return result; }

        ReadKind k4 = ReadKind.Absent;
        if (k3 != ReadKind.Data)
        {
            await Task.Delay(InterWindowDelayMs, ct);
            var (res4, _) = await ReadBlockAsync(master, req, connectMs, slave, new SensorReadWindow(0x04, 42, 2), probeTimeout, ct);
            k4 = res4;
            if (k4 == ReadKind.Unsafe || k4 == ReadKind.ConnectFailed) { result.ConnectionUnsafe = true; return result; }
        }

        if (k3 == ReadKind.Absent && k4 == ReadKind.Absent) return result; // silent

        // Full identification: every installed driver's own identification window.
        for (int d = 0; d < _drivers.Count; d++)
        {
            var driver = _drivers[d];
            var windows = driver.ReadWindows;
            var ident = windows[0];

            var (kind, payload) = await ReadBlockAsync(master, req, connectMs, slave, ident, probeTimeout, ct);
            if (kind == ReadKind.Unsafe || kind == ReadKind.ConnectFailed)
            {
                result.ConnectionUnsafe = true;
                return result;
            }
            if (kind != ReadKind.Data) continue; // absent / not this window

            result.GotData = true;
            var wt0 = driver.ParseWindow(0, payload);
            if (!Plausible(driver, wt0)) continue;

            var combinedBytes = new List<byte>(payload.Length * windows.Count);
            combinedBytes.AddRange(payload);

            double? primary = wt0.PrimaryValue;
            double? secondary = wt0.SecondaryValue;
            int live = HasContent(wt0) ? 1 : 0;
            int extra = 0;

            // Remaining own windows (each on a fresh connection, separated by the
            // RS-485 turn-around delay) - best effort, they only add detail.
            for (int wi = 1; wi < windows.Count; wi++)
            {
                await Task.Delay(InterWindowDelayMs, ct);
                var (k2, pl2) = await ReadBlockAsync(master, req, connectMs, slave, windows[wi], probeTimeout, ct);
                if (k2 != ReadKind.Data) continue;
                combinedBytes.AddRange(pl2);
                var wt = driver.ParseWindow(wi, pl2);
                if (HasContent(wt)) { live++; extra++; }
                primary ??= wt.PrimaryValue;
                secondary ??= wt.SecondaryValue;
            }

            // Corroboration window: served-with-data proves this family.
            bool proofServed = false;
            bool proofHasData = false;
            if (driver.CorroborationWindow is { } proofWin)
            {
                for (int attempt = 0; attempt < DegeneracyProofAttempts && !proofHasData; attempt++)
                {
                    if (attempt > 0) await Task.Delay(InterWindowDelayMs, ct);
                    var (kp, plp) = await ReadBlockAsync(master, req, connectMs, slave, proofWin, probeTimeout, ct);
                    if (kp == ReadKind.Data)
                    {
                        proofServed = true;
                        if (AnyNonZero(plp)) proofHasData = true;
                    }
                }
            }

            bool allZero = (primary ?? 0) == 0 && (secondary ?? 0) == 0;
            if (allZero && !proofHasData)
            {
                _logger?.LogInformation(
                    "scan slave {Slave}: {Driver} REJECTED by degeneracy guard (all zero, proofServed={Served}, proofHasData=false)",
                    slave, driver.DriverKey, proofServed);
                continue;
            }

            result.AcceptedFamilies.Add(driver.DriverKey);
            if (proofHasData) result.ProvenFamilies.Add(driver.DriverKey);

            int unproven = proofHasData ? 0 : 1;
            var rank = new CandidateRank(
                ident.RegisterQuantity,
                unproven,
                live,
                extra,
                d);

            var dto = new ScannedCandidateDto(
                driver.DriverKey,
                driver.DisplayName,
                ident.StartRegister,
                ident.RegisterQuantity,
                primary is null ? null : Math.Round(primary.Value, 2),
                secondary is null ? null : Math.Round(secondary.Value, 2),
                proofServed,
                false);

            result.Candidates.Add(new CandidateEntry(rank, dto, combinedBytes.ToArray()));

            bool strong = proofHasData;
            if (driver.DriverKey == "AOSONG_AQ3485")
            {
                strong = false;
            }
            _logger?.LogDebug(
                "scan slave {Slave}: {Driver} width={Width} unproven={Unproven} live={Live} extra={Extra} proof={Proof} proofData={ProofData} strong={Strong}",
                slave, driver.DriverKey, rank.Width, rank.Unproven, rank.Live, rank.Extra, proofServed, proofHasData, strong);

            if (strong)
            {
                result.StrongVotes[driver.DriverKey] =
                    result.StrongVotes.GetValueOrDefault(driver.DriverKey) + 1;
            }
        }

        return result;
    }

    private enum ReadKind { Absent, WindowInvalid, Unsafe, Data, ConnectFailed }

    private static async Task<(ReadKind Kind, byte[] Payload)> ReadBlockAsync(
        ModbusTcpMaster master,
        SmartScanRequest req,
        int connectMs,
        byte slave,
        SensorReadWindow window,
        int probeTimeout,
        CancellationToken ct)
    {
        try
        {
            using var session = await master.OpenAsync(req.GatewayIp, req.GatewayPort, connectMs, ct);
            var payload = window.FunctionCode == 0x04
                ? await session.ReadInputRegistersAsync(slave, window.StartRegister, window.RegisterQuantity, ct, probeTimeout)
                : await session.ReadHoldingRegistersAsync(slave, window.StartRegister, window.RegisterQuantity, ct, probeTimeout);
            return (ReadKind.Data, payload);
        }
        catch (ModbusException ex)
        {
            if (ex.ExceptionCode == 0x0B) return (ReadKind.Absent, Array.Empty<byte>());
            return (ReadKind.WindowInvalid, Array.Empty<byte>());
        }
        catch (TimeoutException)
        {
            return (ReadKind.Absent, Array.Empty<byte>());
        }
        catch (ModbusConnectException)
        {
            return (ReadKind.ConnectFailed, Array.Empty<byte>());
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            return (ReadKind.Unsafe, Array.Empty<byte>());
        }
    }

    // =================================================================
    // Foreign-window collision hunt (deep mode only)
    // =================================================================
    private async Task<bool> HasForeignWindowCollisionAsync(
        ModbusTcpMaster master,
        SmartScanRequest req,
        int connectMs,
        byte slave,
        string winnerKey,
        int probeTimeout,
        CancellationToken ct)
    {
        var foreign = _drivers
            .Where(d => !string.Equals(d.DriverKey, winnerKey, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.ReadWindows[0])
            .Where(w => w.RegisterQuantity == 2)
            .ToList();
        if (foreign.Count == 0) return false;

        int perWindow = Math.Max(1, ForeignCollisionProbeBudget / foreign.Count);
        int readTimeout = Math.Min(probeTimeout, ForeignCollisionReadTimeoutMs);

        foreach (var win in foreign)
        {
            ModbusTcpSession? session = null;
            int sampled = 0;
            int answered = 0;
            int served = 0;
            try
            {
                while (sampled < perWindow)
                {
                    if (session is null)
                    {
                        session = await master.OpenAsync(req.GatewayIp, req.GatewayPort, connectMs, ct);
                    }
                    else
                    {
                        await Task.Delay(ForeignCollisionWindowJumpMs, ct);
                    }

                    sampled++;
                    try
                    {
                        var payload = win.FunctionCode == 0x04
                            ? await session.ReadInputRegistersAsync(slave, win.StartRegister, win.RegisterQuantity, ct, readTimeout)
                            : await session.ReadHoldingRegistersAsync(slave, win.StartRegister, win.RegisterQuantity, ct, readTimeout);
                        if (payload.Length > 0)
                        {
                            served++;
                            if (AnyNonZero(payload)) answered++;
                        }
                    }
                    catch (ModbusException ex) when (ex.IsProtocolError)
                    {
                        return true;
                    }
                    catch (Exception)
                    {
                        // timeout / absent on this read
                    }

                    if (answered > 0 && answered * 4 <= sampled && sampled >= ForeignCollisionEarlyWitnessSamples)
                        return true;

                    if (served == sampled && sampled >= ForeignCollisionGiveUpSamples)
                        break;
                }

                if (answered > 0 && answered * 4 <= sampled) return true;
            }
            finally
            {
                session?.Dispose();
            }
        }

        return false;
    }

    // =================================================================
    // Helpers
    // =================================================================
    private ISensorDriver? DriverOf(string? key) =>
        key is null ? null : SensorDriverCatalog.GetByKey(key);

    private static bool HasContent(WindowTelemetry w) =>
        (w.PrimaryValue is { } p && p != 0) || (w.SecondaryValue is { } s && s != 0);

    private static bool AnyNonZero(byte[] payload)
    {
        foreach (var b in payload) if (b != 0) return true;
        return false;
    }

    private static bool Plausible(ISensorDriver driver, WindowTelemetry w)
    {
        if (w.ErrorCode != null) return false;

        if (driver is AosongAQ3485Driver)
        {
            return w.PrimaryValue is >= -60 and <= 150
                   && w.SecondaryValue is >= -1 and <= 101;
        }

        if (w.PrimaryValue is { } pv && (!double.IsFinite(pv) || Math.Abs(pv) > 1e7)) return false;
        if (w.SecondaryValue is { } sv && (!double.IsFinite(sv) || Math.Abs(sv) > 1e12)) return false;
        return w.PrimaryValue is not null || w.SecondaryValue is not null;
    }

    private static void MergeOutcome(Dictionary<int, AddressState> states, int slave, SlaveProbe probe)
    {
        if (!states.TryGetValue(slave, out var st))
        {
            st = new AddressState();
            states[slave] = st;
        }

        if (probe.GotData) st.Responded = true;
        foreach (var fam in probe.AcceptedFamilies) st.AcceptedFamilies.Add(fam);
        foreach (var fam in probe.ProvenFamilies) st.ProvenFamilies.Add(fam);
        foreach (var (key, votes) in probe.StrongVotes)
            st.Votes[key] = st.Votes.GetValueOrDefault(key) + votes;

        foreach (var entry in probe.Candidates)
        {
            var idx = st.Candidates.FindIndex(c =>
                string.Equals(c.Dto.DriverKey, entry.Dto.DriverKey, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                if (entry.Rank.CompareTo(st.Candidates[idx].Rank) < 0) st.Candidates[idx] = entry;
            }
            else
            {
                st.Candidates.Add(entry);
            }
        }
    }

    private readonly record struct CandidateRank(int Width, int Unproven, int Live, int Extra, int Order)
        : IComparable<CandidateRank>
    {
        public int CompareTo(CandidateRank other)
        {
            var c = Width.CompareTo(other.Width); if (c != 0) return c;
            c = Unproven.CompareTo(other.Unproven); if (c != 0) return c;
            c = Live.CompareTo(other.Live); if (c != 0) return c;
            c = Extra.CompareTo(other.Extra); if (c != 0) return c;
            return Order.CompareTo(other.Order);
        }
    }

    private readonly record struct CandidateEntry(CandidateRank Rank, ScannedCandidateDto Dto, byte[] RawPayload);

    private sealed class SlaveProbe
    {
        public bool ConnectionUnsafe;
        public bool GotData;
        public readonly List<CandidateEntry> Candidates = new();
        public readonly Dictionary<string, int> StrongVotes = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> AcceptedFamilies = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ProvenFamilies = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AddressState
    {
        public bool Responded;
        public readonly List<CandidateEntry> Candidates = new();
        public readonly Dictionary<string, int> Votes = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> AcceptedFamilies = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ProvenFamilies = new(StringComparer.OrdinalIgnoreCase);
    }
}