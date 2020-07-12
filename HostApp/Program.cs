using HostApp.Processor;
using System;
using System.Diagnostics;
using System.IO;

namespace HostApp {
    class Program {
        static void Main(string[] args) {
            DoZaneTest();
            DoTest("1141 cycle test", "./tests/timingtest-with-end.bin", 0x01000, 0x01000, reportCycleCount: true);
            DoTest("Klaus 6502 functional test", "./tests/6502_functional_test.bin", manualPC: 0x400, reportCycleCount: true, successonPCequals: 0x3469);
            DoBenchmark();
        }

        private static void DoZaneTest() {
            HostPlatform platform = new HostPlatform(new HostLogger());
            int origin = 0x1000;
            platform.Memory[Sim6502.VectorRESET + 0] = (byte)(origin & 0xff);
            platform.Memory[Sim6502.VectorRESET + 1] = (byte)((origin >> 8) & 0xff);
            platform.Memory[origin] = (byte)0x6C;
            platform.Memory[origin + 1] = (byte)0x00;
            platform.Memory[origin + 2] = (byte)0x20;
            platform.Reset(true);
            while (true) {
                platform.Processor.Step();
                Console.WriteLine($"Cycles: {platform.Processor.CycleCount}");
                Console.ReadKey();
            }
        }

        private static void DoTest(string name, string path, int origin = 0, int? resetVector = null, int? manualPC = null, bool? reportCycleCount = false, bool manualStep = false, int? successonPCequals = null) {
            HostPlatform platform = new HostPlatform(new HostLogger());
            if (resetVector.HasValue) {
                platform.LoadProgram(origin, File.ReadAllBytes(path), resetVector.Value);
            }
            else {
                platform.LoadProgram(origin, File.ReadAllBytes(path));
            }
            platform.Reset(true);
            if (manualPC.HasValue) {
                platform.Processor.DebugSetPC(manualPC.Value);
            }
            Stopwatch watch = new Stopwatch();
            watch.Start();
            try {
                while (true) {
                    platform.Processor.Step();
                    if (successonPCequals.HasValue && successonPCequals.Value == platform.Processor.RegisterPC) {
                        break;
                    }
                    if (manualStep) {
                        Console.ReadKey();
                    }
                }
            }
            catch (Exception e) {
                watch.Stop();
                Console.WriteLine(e.Message);
                // minus one cycle for read of 0xBB, which I use to break.
            }
            watch.Stop();
            double mhz = platform.Processor.CycleCount / ((double)watch.ElapsedMilliseconds * 1000);
            if (reportCycleCount.Value == true) {
                Console.WriteLine($"{name} complete in {platform.Processor.CycleCount - 1} cycles ({mhz:F4} mhz).");
            }
            else {
                Console.WriteLine($"{name} complete.");
            }
            Console.ReadKey();
        }

        private static void DoBenchmark() {
            HostPlatform platform = new HostPlatform(null);
            platform.LoadProgram(0x01000, File.ReadAllBytes("./tests/timingtest.bin"), 0x01000);
            Stopwatch watch = new Stopwatch();
            for (int i = 0; i < 10; i++) {
                platform.Reset(true);
                watch.Reset();
                watch.Start();
                while (true) {
                    platform.Processor.Step();
                    if (platform.Processor.CycleCount >= 1141 * 100001) {
                        break;
                    }
                }
                watch.Stop();
                double mhz = platform.Processor.CycleCount / ((double)watch.ElapsedMilliseconds * 1000);
                Console.WriteLine($"{platform.Processor.CycleCount} cycles in {watch.ElapsedMilliseconds / 1000f:F2} seconds ({mhz:F4} mhz).");
            }
            Console.WriteLine($"Benchmark complete.");
            Console.ReadKey();
        }
    }
}
