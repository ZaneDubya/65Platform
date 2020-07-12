using HostApp.Processor;
using System;

namespace HostApp {
    class HostPlatform {
        public readonly Sim6502 Processor;
        private readonly byte[] _Memory = new byte[0x10000];

        public byte[] Memory => _Memory;

        public HostPlatform(HostLogger logger) {
            Processor = new Sim6502(BusAction, null, logger);
        }

        public void Reset(bool resetCycleCount = false) {
            Processor.SignalInputRESB_Low = true;
            Processor.Step();
            Processor.Step();
            Processor.SignalInputRESB_Low = false;
            Processor.Step();
            if (resetCycleCount) {
                Processor.ResetCycleCount();
            }
        }

        private byte BusAction(int addressPins, byte dataPins, bool rwbPin) {
            // NOTE: must multiplex addressPins and current state of address bus...
            if (rwbPin) {
                _Memory[addressPins] = dataPins;
                return 0x00; // not used by caller
            }
            else {
                return _Memory[addressPins];
            }
        }

        /// <summary>
		/// Loads a program into memory and set reset vector.
		/// </summary>
		/// <param name="offset">The offset in memory when loading the program.</param>
		/// <param name="program">The program to be loaded</param>
		/// <param name="initialProgramCounter">The initial PC value, this is the entry point of the program</param>
		public void LoadProgram(int offset, byte[] program, int initialProgramCounter) {
            LoadProgram(offset, program);
            _Memory[Sim6502.VectorRESET + 0] = (byte)(initialProgramCounter & 0xff);
            _Memory[Sim6502.VectorRESET + 1] = (byte)((initialProgramCounter >> 8) & 0xff);
        }

        /// <summary>
        /// Loads a program into memory. Reset is not called.
        /// </summary>
        /// <param name="offset">The offset in memory when loading the program.</param>
        /// <param name="program">The program to be loaded</param>
        public void LoadProgram(int offset, byte[] program) {
            if (offset > _Memory.Length) {
                throw new InvalidOperationException("Offset '{0}' is larger than memory size '{1}'");
            }
            if (program.Length > _Memory.Length + offset) {
                throw new InvalidOperationException(string.Format("Program Size '{0}' Cannot be Larger than Memory Size '{1}' plus offset '{2}'", program.Length, _Memory.Length, offset));
            }
            for (var i = 0; i < program.Length; i++) {
                _Memory[i + offset] = program[i];
            }
        }

    }
}
