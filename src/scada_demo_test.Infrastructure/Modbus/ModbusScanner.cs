using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using scada_demo_test.Application.DTOs;
using scada_demo_test.Application.Interfaces;
using scada_demo_test.Domain.Drivers;

namespace scada_demo_test.Infrastructure.Modbus;

// =====================================================================
// Smart & Fast RS-485 bus scanner (Step 2 / Step 3 of the discovery flow)
// ---------------------------------------------------------------------
// Sweeps Modbus slave addresses and identifies each meter's family ON THE FLY
// from its register signature, with the operator tuning NOTHING. Nothing is
// persisted - a found meter becomes a Sensor only when the operator maps it.
//
// Identification is a ranking, never a first-match: every installed driver's
// own identification window is read, its parser run, and the plausible matches
// are ordered by (Width, Unproven, Live, Extra, Order) - narrowest documented
// window first, a driver that proved itself via its own CorroborationWindow
// next, then the amount of real content. Every candidate that plausibly decode
// at an address is remembered so a genuine tie can be surfaced to the operator
// as AMBIGUOUS instead of silently guessed.
//
// Duplicate slave IDs: two meters on ONE address make its data permanently
// ambiguous, so the address is removed from Found and reported as a conflict.
// Three independent triggers:
//   1. the cheap VOTE gate (>=2 distinct families each winning/proving
//      themselves repeatedly),
//   2. the PROVEN-FAMILIES gate (>=2 distinct families whose OWN corroboration
//      block carried real non-zero data - a zero-filler can never qualify),
//   3. the EXPENSIVE foreign-window collision hunt, only when
//      DeepDuplicateCheck is on.
// =====================================================================
public sealed class ModbusScanner : ISmartScanService
{
    private const byte MinAddress = 1;
    private const byte MaxAddress = 247;

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
    // not lose a genuine match. Raised 4 -> 8: on the real bus the Selec RI-F200-C
    // reads a genuine 0 kW / 0 kWh (CT not connected), so its identity hinges on the
    // @64 voltage block being served; one failed read ties the Kaifeng ghost and
    // flags a perfectly good meter ambiguous.
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
        var skip = new HashSet<int>(request.SkipSlaveAddresses);

        var reqStart = Math.Clamp(request.StartAddress, MinAddress, MaxAddress);
        var reqEnd = Math.Clamp(
            request.EndAddress < request.StartAddress ? request.StartAddress : request.EndAddress,
            MinAddress, MaxAddress);
        var quickEnd = Math.Min(Math.Clamp((int)QuickScanEndAddress, (int)MinAddress, (int)MaxAddress), reqEnd);
        // A scan's TCP connect must be short: the reachability pre-check already told the
        // UI whether the gateway is up, and a dead gateway must be declared unreachable
        // after a handful of probes, not after sweeping the whole bus at the device's own
        // 5s connect budget.
        connectMs = Math.Min(connectMs, 1500);

        // Passes <= 0 => the automatic "Find Sensors" sweep: the full bus in two
        // DISJOINT tiers, both always run. Passes > 0 => Quick: 1..20 only.
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
                        if (skip.Contains(slave))
                        {
                            if (!skipped.Contains(slave)) skipped.Add(slave);
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
                            // Isolate the bad address: skip it and continue the sweep.
                            if (++failureStreak >= UnreachableFailureStreak) { connectivityOk = false; errorCode = "GATEWAY_UNREACHABLE"; abort = true; }
                            continue;
                        }

                        if (probe.ConnectionUnsafe)
                        {
                            if (++failureStreak >= UnreachableFailureStreak) { connectivityOk = false; errorCode = "GATEWAY_UNREACHABLE"; abort = true; }
                            continue;
                        }

                        failureStreak = 0;
                        MergeOutcome(states, slave, probe);
                    }
                }
            }

            // Cheap targeted re-probe of ONLY the addresses that answered: a serial
            // bridge drops a real meter mid-pass, and re-reading a handful of
            // addresses restores the accuracy of a multi-pass sweep at a fraction
            // of its cost. Merged by the same best-rank-wins rule.
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
                            // ignore: the refine pass is best-effort
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

        // ---- Finalize: decide winner / ambiguity / duplicate per address ----
        var found = new List<ScannedMeterDto>();
        var ambiguousSlaves = new List<int>();
        var conflicts = new List<DuplicateSlaveIdConflictDto>();
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
                found.Add(new ScannedMeterDto(
                    slave, null, null, "Unknown responder", 0, 0, null, null, 0,
                    $"Unidentified device (Slave {slave})", null, null, "unknown"));
                continue;
            }

            var ordered = st.Candidates.OrderBy(c => c.Rank).ToList();
            var best = ordered[0];
            var bestDriver = DriverOf(best.Dto.DriverKey);
            if (bestDriver is null) { unknown++; continue; }

            // ---- Duplicate slave ID ----
            var colliding = st.Votes
                .Where(kv => kv.Value >= ConflictVoteThreshold)
                .Select(kv => kv.Key)
                .ToList();

            // Trigger 1: vote gate (>=2 families with repeated strong votes).
            // Trigger 2: proven-families gate (>=2 families whose OWN corroboration
            //            block carried real non-zero data, seen at least once).
            //            A zero-filler (e.g. Selec RI-F200-C) never lands here
            //            because its proofHasData is false.
            // NOTE: the single-observation ProvenFamilies gate was removed - it
            // flagged genuine unique meters (Vortex @2, Selec @4) as duplicates.
            bool duplicate = colliding.Count >= 2;
            bool deepHit = false;

            _logger?.LogInformation(
                "scan slave {Slave}: SUMMARY best={Best} candidates=[{Cands}] voteFamilies=[{Votes}] provenFamilies=[{Proven}]",
                slave,
                best.Dto.DriverKey,
                string.Join(",", ordered.Select(c => $"{c.Dto.DriverKey}(w{c.Rank.Width},u{c.Rank.Unproven},l{c.Rank.Live})")),
                string.Join(",", colliding),
                string.Join(",", st.ProvenFamilies));

            // Trigger 3: expensive foreign-window collision hunt (deep mode only).
            if (!duplicate && request.DeepDuplicateCheck)
            {
                duplicate = await HasForeignWindowCollisionAsync(
                    master, request, connectMs, (byte)slave, best.Dto.DriverKey!, probeTimeout, ct);
                deepHit = duplicate;
            }

            if (duplicate)
            {
                _logger?.LogWarning(
                    "scan slave {Slave}: DUPLICATE declared. byVotes={ByVotes} byDeepCollision={Deep} voteFamilies=[{Votes}] provenFamilies=[{Proven}] acceptedFamilies=[{Accepted}]",
                    slave,
                    colliding.Count >= 2,
                    deepHit,
                    string.Join(",", colliding),
                    string.Join(",", st.ProvenFamilies),
                    string.Join(",", st.AcceptedFamilies));

                var count = st.AcceptedFamilies.Count >= 2 ? st.AcceptedFamilies.Count : Math.Max(2, colliding.Count);
                conflicts.Add(new DuplicateSlaveIdConflictDto(
                    slave,
                    count,
                    null,
                    $"Your {count} sensors are using the same slave ID {slave}. Change all but one to a different slave ID, then scan again."));
                continue;
            }

            // ---- Ambiguity: top two tie on BOTH Width and Unproven ----
            bool ambiguous = ordered.Count >= 2
                             && ordered[0].Rank.Width == ordered[1].Rank.Width
                             && ordered[0].Rank.Unproven == ordered[1].Rank.Unproven;
            if (ambiguous) ambiguousSlaves.Add(slave);

            var meter = new ScannedMeterDto(
                slave,
                bestDriver.DriverKey,
                bestDriver.SimpleName,
                bestDriver.DisplayName,
                best.Dto.StartRegister,
                best.Dto.RegisterQuantity,
                bestDriver.UnitPrimary,
                bestDriver.UnitSecondary,
                bestDriver.DefaultPollIntervalSeconds,
                $"{bestDriver.DisplayName} (Slave {slave})",
                best.Dto.LivePrimary,
                best.Dto.LiveSecondary,
                "identified",
                ambiguous && ordered.Count >= 2,
                ambiguous
                    ? ordered.Select((c, i) => c.Dto with { BestGuess = i == 0 }).ToList()
                    : null);

            found.Add(meter);
        }

        return new SmartScanResultDto(
            request.DeviceId,
            request.DeviceName,
            request.GatewayIp,
            request.GatewayPort,
            probeTimeout,
            totalProbed,
            responding,
            unknown,
            connectivityOk,
            errorCode,
            DateTime.UtcNow,
            found,
            skipped,
            ambiguousSlaves,
            conflicts);
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

        // Presence: one FC03 window and one FC04 window. A silent address costs
        // exactly one probe timeout; an FC03-only OR FC04-only meter is still
        // detected. The two probes run CONCURRENTLY (Task.WhenAny) so a slow or
        // timing-out FC03 on a degraded bus does not add a full probe timeout
        // before the FC04 probe even starts - that is what made a quiet bus
        // feel like it was hanging.
        var probe3 = ReadBlockAsync(master, req, connectMs, slave, new SensorReadWindow(0x03, 0, 2), probeTimeout, ct);
        var probe4 = ReadBlockAsync(master, req, connectMs, slave, new SensorReadWindow(0x04, 42, 2), probeTimeout, ct);
        var (k3, _) = await probe3;
        var (k4, _) = await probe4;
        if (k3 == ReadKind.Unsafe || k4 == ReadKind.Unsafe) { result.ConnectionUnsafe = true; return result; }
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

            // Degeneracy guard: a driver whose own decoded values are ALL exactly
            // zero AND whose corroboration block carries NO real data is rejected
            // (that is what stops a humidity sensor winning as a flowmeter).
            //
            // The test is proofHASdata, NOT proofSERVED. A zero-filler (the Selec
            // RI-F200-C answers EVERY FC04 address with zeros) makes another
            // driver's corroboration block "served" with zeros - that is NOT proof
            // of identity. Only a corroboration block that actually contains
            // non-zero data proves the family is really there. This is what lets
            // the Selec at slave 4 win over the Vortex ghost: the Selec's own @64
            // voltage block carries live data, while the Vortex's @1067 on that
            // same address is answered by the Selec with zeros.
            bool allZero = (primary ?? 0) == 0 && (secondary ?? 0) == 0;
            if (allZero && !proofHasData)
            {
                _logger?.LogInformation(
                    "scan slave {Slave}: {Driver} REJECTED by degeneracy guard (all zero, proofServed={Served}, proofHasData=false)",
                    slave, driver.DriverKey, proofServed);
                continue;
            }

            result.AcceptedFamilies.Add(driver.DriverKey);

            // A family whose OWN corroboration block returned real non-zero data
            // is a genuinely present device. Two such families on one address
            // = two physical meters sharing a slave ID.
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

            result.Candidates.Add(new CandidateEntry(rank, dto));

            // A vote counts only when the family's OWN corroboration block carried
            // real non-zero data. "proofServed" with zeros is the Selec zero-filler
            // answering another driver's window - that must NEVER count as a vote,
            // otherwise ghosts + the real Selec look like a duplicate slave ID.
            bool strong = proofHasData;
            if (driver.DriverKey == "AOSONG_AQ3485")
            {
                // Aosong AQ3485 is never strong - it is always ambiguous
                // because its corroboration block is served by the Selec's
                // zero-fill trap, and the AQ3485 ghost (CT primary rating)
                // always wins the tie-break.
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
            // 0x0B = gateway could not reach the target (absent). 0x02/others =
            // the slave exists but does not serve this window. A protocol/framing
            // error (unit ID mismatch on a replayed cached frame) also means
            // "not this window" - it is NOT a bus-wide fault.
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
            int answered = 0;   // NON-ZERO replies only - a collision witness
            int served = 0;     // ANY reply (incl. zeros) - for the give-up rule
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
                            // Only NON-ZERO replies count as a collision witness.
                            // A zero-filler (the Selec RI-F200-C answers EVERY
                            // FC04 address with zeros) is NOT a second device
                            // racing - counting zeros hid two unique meters.
                            if (AnyNonZero(payload)) answered++;
                        }
                    }
                    catch (ModbusException ex) when (ex.IsProtocolError)
                    {
                        // A malformed frame on a foreign window IS a witness: one
                        // device is racing another for the same request.
                        return true;
                    }
                    catch (Exception)
                    {
                        // timeout / absent on this read - not served
                    }

                    // Early witness: a masked device answers only a few percent, so
                    // the verdict locks long before the block's whole share is spent.
                    if (answered > 0 && answered * 4 <= sampled && sampled >= ForeignCollisionEarlyWitnessSamples)
                        return true;

                    // Give-up: this block is answered ~100% - the address's own
                    // meter or a zero-filler owns it, so it cannot witness a race.
                    // Uses `served` (any reply incl. zeros) so a zero-filler still
                    // short-circuits instead of burning the full 300-sample budget.
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
        key is null ? null : _drivers.FirstOrDefault(d =>
            string.Equals(d.DriverKey, key, StringComparison.OrdinalIgnoreCase));

    private static bool HasContent(WindowTelemetry w) =>
        (w.PrimaryValue is { } p && p != 0) || (w.SecondaryValue is { } s && s != 0);

    private static bool AnyNonZero(byte[] payload)
    {
        foreach (var b in payload) if (b != 0) return true;
        return false;
    }

    // Register-signature sanity: only accept a decoded reading physically
    // possible for the meter family, so a random slave's registers are not
    // misidentified as a flowmeter or a 0..100 % RH sensor.
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

    // =================================================================
    // Nested types
    // =================================================================

    // Ranking, lowest wins. Width first (narrowest documented identification
    // window = most specific signature), then proof (Unproven), then live content,
    // then extra blocks, then catalog order as the deterministic tie-break.
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

    private readonly record struct CandidateEntry(CandidateRank Rank, ScannedCandidateDto Dto);

    private sealed class SlaveProbe
    {
        public bool ConnectionUnsafe;
        public bool GotData;
        public readonly List<CandidateEntry> Candidates = new();
        public readonly Dictionary<string, int> StrongVotes = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> AcceptedFamilies = new(StringComparer.OrdinalIgnoreCase);
        // Families whose own corroboration block carried real non-zero data.
        public readonly HashSet<string> ProvenFamilies = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AddressState
    {
        public bool Responded;
        public readonly List<CandidateEntry> Candidates = new();
        public readonly Dictionary<string, int> Votes = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> AcceptedFamilies = new(StringComparer.OrdinalIgnoreCase);
        // Union of ProvenFamilies across all passes/refine for this address.
        public readonly HashSet<string> ProvenFamilies = new(StringComparer.OrdinalIgnoreCase);
    }
}