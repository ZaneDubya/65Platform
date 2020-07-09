using System;
using HostApp.Emu6502;

namespace HostApp {
    class Hosted6502 : Net6502 {
        private readonly byte[] _Memory = new byte[0x10000];

        public Hosted6502(HostLogger logger) : base(logger) {

        }

        public override byte ReadMemoryValue(int address) {
            IncrementCycleCount();
            return _Memory[address];
        }

        public override byte ReadMemoryValueWithoutCycle(int address) {
            return _Memory[address];
        }

        public override void WriteMemoryValue(int address, byte data) {
            IncrementCycleCount();
            _Memory[address] = data;
        }

        public override void WriteMemoryValueWithoutCycle(int address, byte data) {
            _Memory[address] = data;
        }

        /// <summary>
		/// Loads a program into the processors memory
		/// </summary>
		/// <param name="offset">The offset in memory when loading the program.</param>
		/// <param name="program">The program to be loaded</param>
		/// <param name="initialProgramCounter">The initial PC value, this is the entry point of the program</param>
		public void LoadProgram(int offset, byte[] program, int initialProgramCounter) {
            LoadProgram(offset, program);
            var bytes = BitConverter.GetBytes(initialProgramCounter);
            //Write the initialProgram Counter to the reset vector
            WriteMemoryValue(0xFFFC, bytes[0]);
            WriteMemoryValue(0xFFFD, bytes[1]);
            //Reset the CPU
            Reset();
        }

        /// <summary>
        /// Loads a program into the processors memory
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
            Reset();
        }
    }
}
