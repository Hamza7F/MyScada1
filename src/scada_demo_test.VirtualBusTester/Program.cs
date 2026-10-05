using Microsoft.Extensions.Logging;
using scada_demo_test.Application.DTOs;
using scada_demo_test.Infrastructure.Modbus;

namespace scada_demo_test.VirtualBusTester;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("==========================================================================");
        Console.WriteLine(" ALAM IOT SCADA — DYNAMIC MODBUS SCANNER & VIRTUAL BUS TEST HARNESS");
        Console.WriteLine("==========================================================================");

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "[HH:mm:ss] ";
            });
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger<ModbusScanner>();
        var scanner = new ModbusScanner(logger);

        const int port = 15502;
        await using var server = new VirtualModbusGatewayServer(port);
        server.Start();
        Console.WriteLine($"[INIT] Virtual Modbus TCP Gateway running on 127.0.0.1:{port}\n");

        int passedCount = 0;
        int totalTests = 6;

        try
        {
            // -----------------------------------------------------------------------------------------
            // TEST 1: User's exact real hardware configuration
            // -----------------------------------------------------------------------------------------
            Console.WriteLine(">>> [TEST 1/6] USER'S EXACT HARDWARE SETUP:");
            Console.WriteLine("    EM = Slave 8 (unique)");
            Console.WriteLine("    Humidity = Slave 2 (duplicate with Vortex)");
            Console.WriteLine("    Vortex = Slave 2 (duplicate with Humidity)");
            Console.WriteLine("    Energy (Selec) = Slave 4 (unique)");

            server.SetSensors(new IVirtualSensor[]
            {
                new VirtualKaifengEmSensor(slaveId: 8, totalizerM3: 150.2, flowRateM3H: 12.4),
                new VirtualAosongSensor(slaveId: 2, tempC: 29.1, humidityRh: 56.8),
                new VirtualVortexSensor(slaveId: 2, flowM3H: 0.0, totalizerM3: 0.0),
                new VirtualSelecPowerSensor(slaveId: 4, powerKw: 0.0, energyKwh: 0.0, voltageV: 230.0, ctPrimary: 1000, ctSecondary: 5)
            });

            var scanReq1 = new SmartScanRequest(
                Guid.NewGuid(), "Virtual Gateway", "127.0.0.1", port,
                ConnectTimeoutMs: 500, ProbeTimeoutMs: 150, SkipSlaveAddresses: Array.Empty<int>(),
                StartAddress: 1, EndAddress: 20, Passes: 1, DeepDuplicateCheck: false);

            var res1 = await scanner.ScanAsync(scanReq1);
            PrintScanResult(res1);

            bool t1Pass = res1.Found.Count == 2
                       && res1.Found.Any(f => f.SlaveAddress == 8 && f.DriverKey == "KAIFENG_EM_FLOWMETER")
                       && res1.Found.Any(f => f.SlaveAddress == 4 && f.DriverKey == "SELEC_POWER_METER")
                       && !res1.Found.Any(f => f.SlaveAddress == 2)
                       && res1.DuplicateIdConflicts.Count == 1
                       && res1.DuplicateIdConflicts.Any(c => c.SlaveAddress == 2 && c.MeterCount == 2);

            if (t1Pass)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">>> [TEST 1 RESULT] PASSED! Slave 8=EM (Found), Slave 4=Selec (Found), Slave 2=Conflict (Hidden)\n");
                Console.ResetColor();
                passedCount++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(">>> [TEST 1 RESULT] FAILED!\n");
                Console.ResetColor();
            }

            // -----------------------------------------------------------------------------------------
            // TEST 2: All 4 sensors clean and unique (Slaves 1, 2, 3, 4)
            // -----------------------------------------------------------------------------------------
            Console.WriteLine(">>> [TEST 2/6] ALL 4 UNIQUE SENSORS:");
            Console.WriteLine("    EM = Slave 1, Vortex = Slave 2, Humidity = Slave 3, Energy = Slave 4");

            server.SetSensors(new IVirtualSensor[]
            {
                new VirtualKaifengEmSensor(slaveId: 1),
                new VirtualVortexSensor(slaveId: 2),
                new VirtualAosongSensor(slaveId: 3),
                new VirtualSelecPowerSensor(slaveId: 4)
            });

            var scanReq2 = new SmartScanRequest(
                Guid.NewGuid(), "Virtual Gateway", "127.0.0.1", port,
                ConnectTimeoutMs: 500, ProbeTimeoutMs: 150, SkipSlaveAddresses: Array.Empty<int>(),
                StartAddress: 1, EndAddress: 20, Passes: 1, DeepDuplicateCheck: false);

            var res2 = await scanner.ScanAsync(scanReq2);
            PrintScanResult(res2);

            bool t2Pass = res2.Found.Count == 4
                       && res2.Found.Any(f => f.SlaveAddress == 1 && f.DriverKey == "KAIFENG_EM_FLOWMETER")
                       && res2.Found.Any(f => f.SlaveAddress == 2 && f.DriverKey == "VORTEX_FLOWMETER")
                       && res2.Found.Any(f => f.SlaveAddress == 3 && f.DriverKey == "AOSONG_AQ3485")
                       && res2.Found.Any(f => f.SlaveAddress == 4 && f.DriverKey == "SELEC_POWER_METER")
                       && res2.DuplicateIdConflicts.Count == 0;

            if (t2Pass)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">>> [TEST 2 RESULT] PASSED! All 4 sensors found cleanly with 0 conflicts.\n");
                Console.ResetColor();
                passedCount++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(">>> [TEST 2 RESULT] FAILED!\n");
                Console.ResetColor();
            }

            // -----------------------------------------------------------------------------------------
            // TEST 3: Collision on Energy & EM (Slave 5), while Vortex (Slave 6) and Humidity (Slave 7) are unique
            // -----------------------------------------------------------------------------------------
            Console.WriteLine(">>> [TEST 3/6] COLLISION ON ENERGY & EM (Slave 5):");
            Console.WriteLine("    Energy = Slave 5, EM = Slave 5, Vortex = Slave 6, Humidity = Slave 7");

            server.SetSensors(new IVirtualSensor[]
            {
                new VirtualSelecPowerSensor(slaveId: 5),
                new VirtualKaifengEmSensor(slaveId: 5),
                new VirtualVortexSensor(slaveId: 6),
                new VirtualAosongSensor(slaveId: 7)
            });

            var scanReq3 = new SmartScanRequest(
                Guid.NewGuid(), "Virtual Gateway", "127.0.0.1", port,
                ConnectTimeoutMs: 500, ProbeTimeoutMs: 150, SkipSlaveAddresses: Array.Empty<int>(),
                StartAddress: 1, EndAddress: 20, Passes: 1, DeepDuplicateCheck: false);

            var res3 = await scanner.ScanAsync(scanReq3);
            PrintScanResult(res3);

            bool t3Pass = res3.Found.Count == 2
                       && res3.Found.Any(f => f.SlaveAddress == 6 && f.DriverKey == "VORTEX_FLOWMETER")
                       && res3.Found.Any(f => f.SlaveAddress == 7 && f.DriverKey == "AOSONG_AQ3485")
                       && !res3.Found.Any(f => f.SlaveAddress == 5)
                       && res3.DuplicateIdConflicts.Count == 1
                       && res3.DuplicateIdConflicts.Any(c => c.SlaveAddress == 5 && c.MeterCount == 2);

            if (t3Pass)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">>> [TEST 3 RESULT] PASSED! Slave 5 blocked as conflict, Slaves 6 & 7 found.\n");
                Console.ResetColor();
                passedCount++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(">>> [TEST 3 RESULT] FAILED!\n");
                Console.ResetColor();
            }

            // -----------------------------------------------------------------------------------------
            // TEST 4: Triple Collision (3 meters on Slave 3), EM on Slave 9
            // -----------------------------------------------------------------------------------------
            Console.WriteLine(">>> [TEST 4/6] TRIPLE COLLISION ON SLAVE 3:");
            Console.WriteLine("    Vortex = Slave 3, Humidity = Slave 3, Energy = Slave 3, EM = Slave 9");

            server.SetSensors(new IVirtualSensor[]
            {
                new VirtualVortexSensor(slaveId: 3),
                new VirtualAosongSensor(slaveId: 3),
                new VirtualSelecPowerSensor(slaveId: 3),
                new VirtualKaifengEmSensor(slaveId: 9)
            });

            var scanReq4 = new SmartScanRequest(
                Guid.NewGuid(), "Virtual Gateway", "127.0.0.1", port,
                ConnectTimeoutMs: 500, ProbeTimeoutMs: 150, SkipSlaveAddresses: Array.Empty<int>(),
                StartAddress: 1, EndAddress: 20, Passes: 1, DeepDuplicateCheck: false);

            var res4 = await scanner.ScanAsync(scanReq4);
            PrintScanResult(res4);

            bool t4Pass = res4.Found.Count == 1
                       && res4.Found.Any(f => f.SlaveAddress == 9 && f.DriverKey == "KAIFENG_EM_FLOWMETER")
                       && !res4.Found.Any(f => f.SlaveAddress == 3)
                       && res4.DuplicateIdConflicts.Count == 1
                       && res4.DuplicateIdConflicts.Any(c => c.SlaveAddress == 3 && c.MeterCount >= 2);

            if (t4Pass)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">>> [TEST 4 RESULT] PASSED! Slave 3 blocked (triple collision), Slave 9=EM found.\n");
                Console.ResetColor();
                passedCount++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(">>> [TEST 4 RESULT] FAILED!\n");
                Console.ResetColor();
            }

            // -----------------------------------------------------------------------------------------
            // TEST 5: Quadruple Collision (All 4 meters on Slave 1)
            // -----------------------------------------------------------------------------------------
            Console.WriteLine(">>> [TEST 5/6] QUADRUPLE COLLISION ON SLAVE 1:");
            Console.WriteLine("    All 4 meters configured with Slave ID 1");

            server.SetSensors(new IVirtualSensor[]
            {
                new VirtualKaifengEmSensor(slaveId: 1),
                new VirtualAosongSensor(slaveId: 1),
                new VirtualVortexSensor(slaveId: 1),
                new VirtualSelecPowerSensor(slaveId: 1)
            });

            var scanReq5 = new SmartScanRequest(
                Guid.NewGuid(), "Virtual Gateway", "127.0.0.1", port,
                ConnectTimeoutMs: 500, ProbeTimeoutMs: 150, SkipSlaveAddresses: Array.Empty<int>(),
                StartAddress: 1, EndAddress: 20, Passes: 1, DeepDuplicateCheck: false);

            var res5 = await scanner.ScanAsync(scanReq5);
            PrintScanResult(res5);

            bool t5Pass = res5.Found.Count == 0
                       && res5.DuplicateIdConflicts.Count == 1
                       && res5.DuplicateIdConflicts.Any(c => c.SlaveAddress == 1);

            if (t5Pass)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">>> [TEST 5 RESULT] PASSED! All 4 meters colliding on Slave 1 blocked; 0 found.\n");
                Console.ResetColor();
                passedCount++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(">>> [TEST 5 RESULT] FAILED!\n");
                Console.ResetColor();
            }

            // -----------------------------------------------------------------------------------------
            // TEST 6: Complex 8-Sensor Industrial RS-485 Bus Setup
            // -----------------------------------------------------------------------------------------
            Console.WriteLine(">>> [TEST 6/6] 8-SENSOR COMPLEX RS-485 BUS:");
            Console.WriteLine("    Slave 10: Humidity (unique)");
            Console.WriteLine("    Slave 11: Vortex (unique)");
            Console.WriteLine("    Slave 12: EM Flowmeter (unique)");
            Console.WriteLine("    Slave 13: Selec Energy (unique)");
            Console.WriteLine("    Slave 14: Humidity + Vortex (collision)");
            Console.WriteLine("    Slave 15: EM + Selec Energy (collision)");

            server.SetSensors(new IVirtualSensor[]
            {
                new VirtualAosongSensor(slaveId: 10, tempC: 24.5, humidityRh: 45.0),
                new VirtualVortexSensor(slaveId: 11, flowM3H: 22.0, totalizerM3: 1200.0),
                new VirtualKaifengEmSensor(slaveId: 12, totalizerM3: 540.0, flowRateM3H: 33.2),
                new VirtualSelecPowerSensor(slaveId: 13, powerKw: 45.2, energyKwh: 3400.0),
                new VirtualAosongSensor(slaveId: 14, tempC: 26.0, humidityRh: 50.0),
                new VirtualVortexSensor(slaveId: 14, flowM3H: 15.0, totalizerM3: 800.0),
                new VirtualKaifengEmSensor(slaveId: 15, totalizerM3: 110.0, flowRateM3H: 5.5),
                new VirtualSelecPowerSensor(slaveId: 15, powerKw: 12.0, energyKwh: 500.0)
            });

            var scanReq6 = new SmartScanRequest(
                Guid.NewGuid(), "Virtual Gateway", "127.0.0.1", port,
                ConnectTimeoutMs: 500, ProbeTimeoutMs: 150, SkipSlaveAddresses: Array.Empty<int>(),
                StartAddress: 10, EndAddress: 20, Passes: 1, DeepDuplicateCheck: false);

            var res6 = await scanner.ScanAsync(scanReq6);
            PrintScanResult(res6);

            bool t6Pass = res6.Found.Count == 4
                       && res6.Found.Any(f => f.SlaveAddress == 10 && f.DriverKey == "AOSONG_AQ3485")
                       && res6.Found.Any(f => f.SlaveAddress == 11 && f.DriverKey == "VORTEX_FLOWMETER")
                       && res6.Found.Any(f => f.SlaveAddress == 12 && f.DriverKey == "KAIFENG_EM_FLOWMETER")
                       && res6.Found.Any(f => f.SlaveAddress == 13 && f.DriverKey == "SELEC_POWER_METER")
                       && !res6.Found.Any(f => f.SlaveAddress == 14)
                       && !res6.Found.Any(f => f.SlaveAddress == 15)
                       && res6.DuplicateIdConflicts.Count == 2
                       && res6.DuplicateIdConflicts.Any(c => c.SlaveAddress == 14)
                       && res6.DuplicateIdConflicts.Any(c => c.SlaveAddress == 15);

            if (t6Pass)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(">>> [TEST 6 RESULT] PASSED! Slaves 10..13 unique found, Slaves 14 & 15 conflicts blocked.\n");
                Console.ResetColor();
                passedCount++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(">>> [TEST 6 RESULT] FAILED!\n");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[FATAL ERROR] {ex}");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine("==========================================================================");
        Console.WriteLine($" TEST SUITE SUMMARY: {passedCount} / {totalTests} PASSED");
        Console.WriteLine("==========================================================================");

        return passedCount == totalTests ? 0 : 1;
    }

    private static void PrintScanResult(SmartScanResultDto res)
    {
        Console.WriteLine($"    [SCAN SUMMARY] Responding={res.RespondingSlaves}, Found={res.Found.Count}, Conflicts={res.DuplicateIdConflicts.Count}, Unknown={res.UnknownResponders}");
        foreach (var m in res.Found)
        {
            Console.WriteLine($"      -> FOUND: Slave {m.SlaveAddress} | {m.DisplayName} ({m.DriverKey}) | Live: {m.LivePrimary} {m.UnitPrimary} / {m.LiveSecondary} {m.UnitSecondary}");
        }
        foreach (var c in res.DuplicateIdConflicts)
        {
            Console.WriteLine($"      -> CONFLICT: Slave {c.SlaveAddress} | {c.MeterCount} Devices Colliding: [{string.Join(", ", c.CollidingMeterNames ?? new())}] | Msg: {c.Message}");
        }
    }
}
