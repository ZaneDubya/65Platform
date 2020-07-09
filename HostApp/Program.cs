using System;
using System.Diagnostics;
using System.IO;

namespace HostApp {
    class Program {
        static void Main(string[] args) {
            DoCycleTest();
            DoBenchmark();
            Console.WriteLine($"Benchmark complete.");
            Console.ReadKey();
        }

        private static void DoCycleTest() {
            Hosted6502 proc = new Hosted6502(new HostLogger());
            proc.LoadProgram(0x01000, File.ReadAllBytes("./tests/timingtest-with-end.bin"), 0x01000);
            try {
                while (true) {
                    proc.NextStep();
                    if (proc.CycleCount >= 100000000) {
                        break;
                    }
                }
            }
            catch (Exception e) {
                Console.WriteLine(e.Message);
                // minus one cycle for read of 0xBB, which I use to break.
                Console.WriteLine($"Test completed in {proc.CycleCount - 1} cycles (1141 is correct).");
            }
            Console.ReadKey();
        }

        private static void DoBenchmark() {
            Hosted6502 proc = new Hosted6502(null);
            proc.LoadProgram(0x01000, File.ReadAllBytes("./tests/timingtest.bin"), 0x01000);
            Stopwatch watch = new Stopwatch();
            for (int i = 0; i < 10; i++) {
                proc.Reset();
                watch.Reset();
                watch.Start();
                while (true) {
                    proc.NextStep();
                    if (proc.CycleCount >= 1141 * 100001) {
                        break;
                    }
                }
                watch.Stop();
                double seconds = watch.ElapsedMilliseconds / 1000f;
                double mhz = proc.CycleCount / (seconds * 1000000);
                Console.WriteLine($"{proc.CycleCount} cycles in {seconds:F2} seconds ({mhz} mhz).");
            }
        }
    }
}
