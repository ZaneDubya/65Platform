﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace HostApp.Processor {
    /// <summary>
    /// An Implementation of a 6502 Processor, as implemented at aaronmell/6502Net
    /// </summary>
    sealed internal class Sim6502 {

        /// <summary>
        /// Drives 25 pins onto platform bus: 16 address, 8 data, and rwb.
        /// </summary>
        /// <param name="addressPins">16-bit address</param>
        /// <param name="dataPins">8-bit data, only used if rwbPin is true</param>
        /// <param name="rwbPin">If false (high), 65c02 is reading from the data bus, and the callee will use the returned value as the data bus contents. If true (low), 65c02 is writing to the data bus, and the caller will ignore any returned value.</param>
        /// <returns>The 8-bit contents of the platform's data bus. Only used if rwbPin is false (high).</returns>
        public delegate byte BusActionHandler(int addressPins, byte dataPins, bool rwbPin);

        public const int VectorNMI = 0xFFFA;
        public const int VectorRESET = 0xFFFC;
        public const int VectorIRQ = 0xFFFE;
        public const bool LOGIC_HIGH = false;
        public const bool LOGIC_LOW = true;

        public int CycleCount => _CycleCount;

        public override string ToString() => $"Net6502: {_CycleCount} cycles";

        private readonly HostLogger _Logger;
        private int _PC;
        private int _SP;
        private bool _PendingReset;
        private bool _PendingNMI;
        private bool _PinRESB_Low = false;
        private bool _PinNMIB_Low = false;
        private int _CycleCount;

        // === Actions ===============================================================================================
        // ===========================================================================================================

        /// <summary>
        /// Called when the cycle count is incremented
        /// </summary>
        private readonly Action _OnCycle;

        private readonly BusActionHandler _OnBusAction;

        // === Pins ==================================================================================================
        // ===========================================================================================================

        /// <summary>
        /// Input. Drive low (true) to signal reset, then drive high for normal operation.
        /// A real 65c02 requires RESB to be low for at least two PHI2 cycles, but this emulator is fully reset by even a brief pulse.
        /// </summary>
        public bool PinRESB_Low {
            get => _PinRESB_Low;
            set {
                if (!value && _PinRESB_Low) {
                    _PendingReset = true;
                }
                _PinRESB_Low = value;
            }
        }

        /// <summary>
        /// Input. Drive low (true) from high (false) to signal Non Maskable Interrupt.
        /// Unlike IRQB, this is an edge-triggered signal, not a level-triggered one.
        /// </summary>
        public bool PinNMI_Low {
            get => _PinNMIB_Low;
            set {
                if (value && !_PinNMIB_Low) {
                    _PendingNMI = true;
                }
                _PinNMIB_Low = value;
            }
        }

        /// <summary>
        /// Input. Drive low (true) to signal an Interrupt Request.
        /// If the FlagI bit is clear, the Emulator will start its IRQ handler so long as IRQB is low.
        /// </summary>
        public bool PinIRQB_Low = false;

        /// <summary>
        /// Input. Drive low (true) to float 6502's bus pins (A, D, RWB), drivet high (false) to enable them.
        /// Driving low does not stop the CPU from executing! If you use this, you may want to use RDY as well!
        /// </summary>
        public bool PinBE_Low = false;

        // === Properties ============================================================================================
        // Note from aaronmell: all properties are public to facilitate ease of debugging/testing.
        // ===========================================================================================================

        /// <summary>
        /// The Accumulator. This value is implemented as an integer intead of a byte.
        /// This is done so we can detect wrapping of the value and set the correct number of cycles.
        /// </summary>
        public int RegisterA { get; private set; }

        /// <summary>
        /// The X Index Register
        /// </summary>
        public int RegisterX { get; private set; }

        /// <summary>
        /// The Y Index Register
        /// </summary>
        public int RegisterY { get; private set; }

        /// <summary>
        /// The instruction register - current Op Code being executed by the system
        /// </summary>
        public int RegisterIR { get; private set; }

        /// <summary>
        /// The disassembly of the current operation. This value is only set when the CPU is built in debug mode.
        /// </summary>
        public Disassembly CurrentDisassembly { get; private set; }
        /// <summary>
        /// Points to the Current Address of the instruction being executed by the system. 
        /// The PC wraps when the value is greater than 65535, or less than 0. 
        /// </summary>
        public int RegisterPC {
            get { return _PC; }
            private set { _PC = WrapProgramCounter(value); }
        }
        /// <summary>
        /// Points to the Current Position of the Stack.
        /// This value is a 00-FF value but is offset to point to the location in memory where the stack resides.
        /// </summary>
        public int RegisterSP {
            get { return _SP; }
            private set {
                if (value > 0xFF)
                    _SP = value - 0x100;
                else if (value < 0x00)
                    _SP = value + 0x100;
                else
                    _SP = value;
            }
        }

        /// <summary>
        /// This is the carry flag. when adding, if the result is greater than 255 or 99 in BCD Mode, then this bit is enabled. 
        /// In subtraction this is reversed and set to false if a borrow is required IE the result is less than 0
        /// </summary>
        public bool FlagC { get; private set; }

        /// <summary>
        /// Is true if one of the registers is set to zero.
        /// </summary>
        public bool FlagZ { get; private set; }

        /// <summary>
        /// If set, interrupts except for NMI are disabled.
        /// This flag is turned on during a reset to prevent an interrupt from occuring during startup/Initialization.
        /// If this flag is true, then the IRQ pin is ignored.
        /// </summary>
        public bool FlagI { get; private set; }

        /// <summary>
        /// Binary Coded Decimal Mode is set/cleared via this flag.
        /// when this mode is in effect, a byte represents a number from 0-99. 
        /// </summary>
        public bool FlagD { get; private set; }

        /// <summary>
        /// This property is set when an overflow occurs. An overflow happens if the high bit(7) changes during the operation. Remember that values from 128-256 are negative values
        /// as the high bit is set to 1.
        /// Examples:
        /// 64 + 64 = -128 
        /// -128 + -128 = 0
        /// </summary>
        public bool FlagV { get; private set; }
        /// <summary>
        /// Set to true if the result of an operation is negative in ADC and SBC operations. 
        /// Remember that 128-256 represent negative numbers when doing signed math.
        /// In shift operations the sign holds the carry.
        /// </summary>
        public bool FlagN { get; private set; }

        // === Public Methods ========================================================================================
        // ===========================================================================================================
        
        /// <summary>
        /// Default Constructor, Instantiates a new instance of the processor.
        /// </summary>
        public Sim6502(BusActionHandler onBusAction, Action onCycle = null, HostLogger logger = null) {
            _OnBusAction = onBusAction;
            _OnCycle = onCycle;
            _Logger = logger;
            RegisterSP = 0x100;
        }

        /// <summary>
        /// Debug only - allows setting PC manually.
        /// </summary>
        /// <param name="pc"></param>
        public void DebugSetPC(int pc) {
            RegisterPC = pc;
        }

        /// <summary>
        /// Resets the Cycle Count back to 0
        /// </summary>
	    public void ResetCycleCount() {
            _CycleCount = 0;
        }

        /// <summary>
        /// Performs the next step on the processor.
        /// </summary>
        public void Step() {
            if (!_PendingReset && !PinRESB_Low) {
                // T0
                RegisterIR = ReadMemoryValue(RegisterPC);
                SetDisassembly();
                RegisterPC++;
                // T1...T6 (including additional cycles for branch taken, page cross, etc.)
                ExecuteOpCode();
            }
            if (_PendingReset) {
                ProcessReset();
            }
            else if (_PendingNMI) {
                ProcessNMI();
            }
            else if (PinIRQB_Low && !FlagI) {
                ProcessIRQ();
            }
        }

        /// <summary>
        /// Initializes the processor to its default state.
        /// </summary>
        private void ProcessReset() {
            RegisterSP = 0x1FD;
            // Set the Program Counter to the Reset Vector Address.
            RegisterPC = 0xFFFC;
            // Reset the Program Counter to the Address contained in the Reset Vector
            RegisterPC = ReadMemoryValueWithoutCycle(RegisterPC) | (ReadMemoryValueWithoutCycle(RegisterPC + 1) << 8);
            FlagI = true;
            _PendingReset = false;
            _PendingNMI = false;
        }

        /// <summary>
        /// Increments the Cycle Count, causes OnCycle() to fire.
        /// </summary>
        private void IncrementCycleCount() {
            _CycleCount++;
            _OnCycle?.Invoke();
        }

        // === Memory Methods ========================================================================================
        // ===========================================================================================================

        /// <summary>
        /// Returns the byte at the given address.
        /// </summary>
        /// <param name="address">The address to return</param>
        /// <returns>the byte being returned</returns>
        private byte ReadMemoryValue(int address) {
            byte value = ReadMemoryValueWithoutCycle(address);
            IncrementCycleCount();
            return value;
        }

        /// <summary>
        /// Returns the byte at a given address without incrementing the cycle.
        /// </summary>
        private byte ReadMemoryValueWithoutCycle(int address) {
            byte value = 0xEA; // NOP
            if (_OnBusAction != null) {
                value = _OnBusAction.Invoke(PinBE_Low ? 0 : address, 0, LOGIC_HIGH);
            }
            return value;
        }

        /// <summary>
        /// Writes data to the given address.
        /// </summary>
        /// <param name="address">The address to write data to</param>
        /// <param name="data">The data to write</param>
        private void WriteMemoryValue(int address, byte data) {
            WriteMemoryValueWithoutCycle(address, data);
            IncrementCycleCount();
        }

        /// <summary>
        /// Writes data to the given address without incrementing the cycle.
        /// </summary>
        private void WriteMemoryValueWithoutCycle(int address, byte data) {
            if (!PinBE_Low && _OnBusAction != null) {
                _OnBusAction(address, data, LOGIC_LOW);
            }
        }

        // === Processor Methods =====================================================================================
        // ===========================================================================================================

        /// <summary>
        /// Executes an Opcode
        /// </summary>
        private void ExecuteOpCode() {
            //The x+ cycles denotes that if a page wrap occurs, then an additional cycle is consumed.
            //The x++ cycles denotes that 1 cycle is added when a branch occurs and it on the same page, and two cycles are added if its on a different page./
            //This is handled inside the GetValueFromMemory Method
            switch (RegisterIR) {
                // --- Add / Subtract Operations ---------------------------------------------------------------------
                //ADC Add With Carry, Indexed Indirect, 2 Bytes, 6 Cycles
                case 0x61:
                    AddWithCarryOperation(EAddressingMode.IndirectX);
                    break;
                //ADC Add With Carry, Zero Page, 2 Bytes, 3 Cycles
                case 0x65:
                    AddWithCarryOperation(EAddressingMode.ZeroPage);
                    break;
                //ADC Add With Carry, Immediate, 2 Bytes, 2 Cycles
                case 0x69:
                    AddWithCarryOperation(EAddressingMode.Immediate);
                    break;
                //ADC Add With Carry, Absolute, 3 Bytes, 4 Cycles
                case 0x6D:
                    AddWithCarryOperation(EAddressingMode.Absolute);
                    break;
                //ADC Add With Carry, Indexed Indirect, 2 Bytes, 5+ Cycles
                case 0x71:
                    AddWithCarryOperation(EAddressingMode.IndirectY);
                    break;
                //ADC Add With Carry, Zero Page X, 2 Bytes, 4 Cycles
                case 0x75:
                    AddWithCarryOperation(EAddressingMode.ZeroPageX);
                    break;
                //ADC Add With Carry, Absolute Y, 3 Bytes, 4+ Cycles
                case 0x79:
                    AddWithCarryOperation(EAddressingMode.AbsoluteY);
                    break;
                //ADC Add With Carry, Absolute X, 3 Bytes, 4+ Cycles
                case 0x7D:
                    AddWithCarryOperation(EAddressingMode.AbsoluteX);
                    break;
                //SBC Subtract with Borrow, Immediate, 2 Bytes, 2 Cycles
                case 0xE9: {
                        SubtractWithBorrowOperation(EAddressingMode.Immediate);
                        break;
                    }
                //SBC Subtract with Borrow, Zero Page, 2 Bytes, 3 Cycles
                case 0xE5: {
                        SubtractWithBorrowOperation(EAddressingMode.ZeroPage);
                        break;
                    }
                //SBC Subtract with Borrow, Zero Page X, 2 Bytes, 4 Cycles
                case 0xF5: {
                        SubtractWithBorrowOperation(EAddressingMode.ZeroPageX);
                        break;
                    }
                //SBC Subtract with Borrow, Absolute, 3 Bytes, 4 Cycles
                case 0xED: {
                        SubtractWithBorrowOperation(EAddressingMode.Absolute);
                        break;
                    }
                //SBC Subtract with Borrow, Absolute X, 3 Bytes, 4+ Cycles
                case 0xFD: {
                        SubtractWithBorrowOperation(EAddressingMode.AbsoluteX);
                        break;
                    }
                //SBC Subtract with Borrow, Absolute Y, 3 Bytes, 4+ Cycles
                case 0xF9: {
                        SubtractWithBorrowOperation(EAddressingMode.AbsoluteY);
                        break;
                    }
                //SBC Subtract with Borrow, Indexed Indirect, 2 Bytes, 6 Cycles
                case 0xE1: {
                        SubtractWithBorrowOperation(EAddressingMode.IndirectX);
                        break;
                    }
                //SBC Subtract with Borrow, Indexed Indirect, 2 Bytes, 5+ Cycles
                case 0xF1: {
                        SubtractWithBorrowOperation(EAddressingMode.IndirectY);
                        break;
                    }

                // === Branch Operations =============================================================================
                // ===================================================================================================

                //BCC Branch if Carry is Clear, Relative, 2 Bytes, 2++ Cycles
                case 0x90: {
                        BranchOperation(!FlagC);
                        break;

                    }
                //BCS Branch if Carry is Set, Relative, 2 Bytes, 2++ Cycles
                case 0xB0: {
                        BranchOperation(FlagC);
                        break;
                    }
                //BEQ Branch if Zero is Set, Relative, 2 Bytes, 2++ Cycles
                case 0xF0: {
                        BranchOperation(FlagZ);
                        break;
                    }

                // BMI Branch if Negative Set
                case 0x30: {
                        BranchOperation(FlagN);
                        break;
                    }
                //BNE Branch if Zero is Not Set, Relative, 2 Bytes, 2++ Cycles
                case 0xD0: {
                        BranchOperation(!FlagZ);
                        break;
                    }
                // BPL Branch if Negative Clear, 2 Bytes, 2++ Cycles
                case 0x10: {
                        BranchOperation(!FlagN);
                        break;
                    }
                // BVC Branch if Overflow Clear, 2 Bytes, 2++ Cycles
                case 0x50: {
                        BranchOperation(!FlagV);
                        break;
                    }
                // BVS Branch if Overflow Set, 2 Bytes, 2++ Cycles
                case 0x70: {
                        BranchOperation(FlagV);
                        break;
                    }

                // === BitWise Comparison Operations ============================================================================================
                // ==============================================================================================================================

                //AND Compare Memory with Accumulator, Immediate, 2 Bytes, 2 Cycles
                case 0x29: {
                        AndOperation(EAddressingMode.Immediate);
                        break;
                    }
                //AND Compare Memory with Accumulator, Zero Page, 2 Bytes, 3 Cycles
                case 0x25: {
                        AndOperation(EAddressingMode.ZeroPage);
                        break;
                    }
                //AND Compare Memory with Accumulator, Zero PageX, 2 Bytes, 3 Cycles
                case 0x35: {
                        AndOperation(EAddressingMode.ZeroPageX);
                        break;
                    }
                //AND Compare Memory with Accumulator, Absolute,  3 Bytes, 4 Cycles
                case 0x2D: {
                        AndOperation(EAddressingMode.Absolute);
                        break;
                    }
                //AND Compare Memory with Accumulator, AbsolueteX 3 Bytes, 4+ Cycles
                case 0x3D: {
                        AndOperation(EAddressingMode.AbsoluteX);
                        break;
                    }
                //AND Compare Memory with Accumulator, AbsoluteY, 3 Bytes, 4+ Cycles
                case 0x39: {
                        AndOperation(EAddressingMode.AbsoluteY);
                        break;
                    }
                //AND Compare Memory with Accumulator, IndexedIndirect, 2 Bytes, 6 Cycles
                case 0x21: {
                        AndOperation(EAddressingMode.IndirectX);
                        break;
                    }
                //AND Compare Memory with Accumulator, IndirectIndexed, 2 Bytes, 5 Cycles
                case 0x31: {
                        AndOperation(EAddressingMode.IndirectY);
                        break;
                    }
                //BIT Compare Memory with Accumulator, Zero Page, 2 Bytes, 3 Cycles
                case 0x24: {
                        BitOperation(EAddressingMode.ZeroPage);
                        break;
                    }
                //BIT Compare Memory with Accumulator, Absolute, 2 Bytes, 4 Cycles
                case 0x2C: {
                        BitOperation(EAddressingMode.Absolute);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, Immediate, 2 Bytes, 2 Cycles
                case 0x49: {
                        EorOperation(EAddressingMode.Immediate);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, Zero Page, 2 Bytes, 3 Cycles
                case 0x45: {
                        EorOperation(EAddressingMode.ZeroPage);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, Zero Page X, 2 Bytes, 4 Cycles
                case 0x55: {
                        EorOperation(EAddressingMode.ZeroPageX);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, Absolute, 3 Bytes, 4 Cycles
                case 0x4D: {
                        EorOperation(EAddressingMode.Absolute);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, Absolute X, 3 Bytes, 4+ Cycles
                case 0x5D: {
                        EorOperation(EAddressingMode.AbsoluteX);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, Absolute Y, 3 Bytes, 4+ Cycles
                case 0x59: {
                        EorOperation(EAddressingMode.AbsoluteY);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, IndexedIndirect, 2 Bytes 6 Cycles
                case 0x41: {
                        EorOperation(EAddressingMode.IndirectX);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, IndirectIndexed, 2 Bytes 5 Cycles
                case 0x51: {
                        EorOperation(EAddressingMode.IndirectY);
                        break;
                    }
                //ORA Compare Memory with Accumulator, Immediate, 2 Bytes, 2 Cycles
                case 0x09: {
                        OrOperation(EAddressingMode.Immediate);
                        break;
                    }
                //ORA Compare Memory with Accumulator, Zero Page, 2 Bytes, 2 Cycles
                case 0x05: {
                        OrOperation(EAddressingMode.ZeroPage);
                        break;
                    }
                //ORA Compare Memory with Accumulator, Zero PageX, 2 Bytes, 4 Cycles
                case 0x15: {
                        OrOperation(EAddressingMode.ZeroPageX);
                        break;
                    }
                //ORA Compare Memory with Accumulator, Absolute,  3 Bytes, 4 Cycles
                case 0x0D: {
                        OrOperation(EAddressingMode.Absolute);
                        break;
                    }
                //ORA Compare Memory with Accumulator, AbsolueteX 3 Bytes, 4+ Cycles
                case 0x1D: {
                        OrOperation(EAddressingMode.AbsoluteX);
                        break;
                    }
                //ORA Compare Memory with Accumulator, AbsoluteY, 3 Bytes, 4+ Cycles
                case 0x19: {
                        OrOperation(EAddressingMode.AbsoluteY);
                        break;
                    }
                //ORA Compare Memory with Accumulator, IndexedIndirect, 2 Bytes, 6 Cycles
                case 0x01: {
                        OrOperation(EAddressingMode.IndirectX);
                        break;
                    }
                //ORA Compare Memory with Accumulator, IndirectIndexed, 2 Bytes, 5 Cycles
                case 0x11: {
                        OrOperation(EAddressingMode.IndirectY);
                        break;
                    }

                // === Clear Flag Operations ====================================================================================================
                // ==============================================================================================================================

                //CLC Clear Carry Flag, Implied, 1 Byte, 2 Cycles
                case 0x18: {
                        FlagC = false;
                        IncrementCycleCount();
                        break;
                    }
                //CLD Clear Decimal Flag, Implied, 1 Byte, 2 Cycles
                case 0xD8: {
                        FlagD = false;
                        IncrementCycleCount();
                        break;

                    }
                //CLI Clear Interrupt Flag, Implied, 1 Byte, 2 Cycles
                case 0x58: {
                        FlagI = false;
                        IncrementCycleCount();
                        break;

                    }
                //CLV Clear Overflow Flag, Implied, 1 Byte, 2 Cycles
                case 0xB8: {
                        FlagV = false;
                        IncrementCycleCount();
                        break;
                    }

                // === Compare Operations =======================================================================================================
                // ==============================================================================================================================

                //CMP Compare Accumulator with Memory, Immediate, 2 Bytes, 2 Cycles
                case 0xC9: {
                        CompareOperation(EAddressingMode.Immediate, RegisterA);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Zero Page, 2 Bytes, 3 Cycles
                case 0xC5: {
                        CompareOperation(EAddressingMode.ZeroPage, RegisterA);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Zero Page x, 2 Bytes, 4 Cycles
                case 0xD5: {
                        CompareOperation(EAddressingMode.ZeroPageX, RegisterA);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Absolute, 3 Bytes, 4 Cycles
                case 0xCD: {
                        CompareOperation(EAddressingMode.Absolute, RegisterA);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Absolute X, 2 Bytes, 4 Cycles
                case 0xDD: {
                        CompareOperation(EAddressingMode.AbsoluteX, RegisterA);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Absolute Y, 2 Bytes, 4 Cycles
                case 0xD9: {
                        CompareOperation(EAddressingMode.AbsoluteY, RegisterA);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Indirect X, 2 Bytes, 6 Cycles
                case 0xC1: {
                        CompareOperation(EAddressingMode.IndirectX, RegisterA);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Indirect Y, 2 Bytes, 5 Cycles
                case 0xD1: {
                        CompareOperation(EAddressingMode.IndirectY, RegisterA);
                        break;
                    }
                //CPX Compare Accumulator with X Register, Immediate, 2 Bytes, 2 Cycles
                case 0xE0: {
                        CompareOperation(EAddressingMode.Immediate, RegisterX);
                        break;
                    }
                //CPX Compare Accumulator with X Register, Zero Page, 2 Bytes, 3 Cycles
                case 0xE4: {
                        CompareOperation(EAddressingMode.ZeroPage, RegisterX);
                        break;
                    }
                //CPX Compare Accumulator with X Register, Absolute, 3 Bytes, 4 Cycles
                case 0xEC: {
                        CompareOperation(EAddressingMode.Absolute, RegisterX);
                        break;
                    }
                //CPY Compare Accumulator with Y Register, Immediate, 2 Bytes, 2 Cycles
                case 0xC0: {
                        CompareOperation(EAddressingMode.Immediate, RegisterY);
                        break;
                    }
                //CPY Compare Accumulator with Y Register, Zero Page, 2 Bytes, 3 Cycles
                case 0xC4: {
                        CompareOperation(EAddressingMode.ZeroPage, RegisterY);
                        break;
                    }
                //CPY Compare Accumulator with Y Register, Absolute, 3 Bytes, 4 Cycles
                case 0xCC: {
                        CompareOperation(EAddressingMode.Absolute, RegisterY);
                        break;
                    }

                // === Increment/Decrement Operations ===========================================================================================
                // ==============================================================================================================================

                //DEC Decrement Memory by One, Zero Page, 2 Bytes, 5 Cycles
                case 0xC6: {
                        ChangeMemoryByOne(EAddressingMode.ZeroPage, true);
                        break;
                    }
                //DEC Decrement Memory by One, Zero Page X, 2 Bytes, 6 Cycles
                case 0xD6: {
                        ChangeMemoryByOne(EAddressingMode.ZeroPageX, true);
                        break;
                    }
                //DEC Decrement Memory by One, Absolute, 3 Bytes, 6 Cycles
                case 0xCE: {
                        ChangeMemoryByOne(EAddressingMode.Absolute, true);
                        break;
                    }
                //DEC Decrement Memory by One, Absolute X, 3 Bytes, 7 Cycles
                case 0xDE: {
                        ChangeMemoryByOne(EAddressingMode.AbsoluteX, true);
                        IncrementCycleCount();
                        break;
                    }
                //DEX Decrement X Register by One, Implied, 1 Bytes, 2 Cycles
                case 0xCA: {
                        ChangeRegister(true, true);
                        break;
                    }
                //DEY Decrement Y Register by One, Implied, 1 Bytes, 2 Cycles
                case 0x88: {
                        ChangeRegister(false, true);
                        break;
                    }
                //INC Increment Memory by One, Zero Page, 2 Bytes, 5 Cycles
                case 0xE6: {
                        ChangeMemoryByOne(EAddressingMode.ZeroPage, false);
                        break;
                    }
                //INC Increment Memory by One, Zero Page X, 2 Bytes, 6 Cycles
                case 0xF6: {
                        ChangeMemoryByOne(EAddressingMode.ZeroPageX, false);
                        break;
                    }
                //INC Increment Memory by One, Absolute, 3 Bytes, 6 Cycles
                case 0xEE: {
                        ChangeMemoryByOne(EAddressingMode.Absolute, false);
                        break;
                    }
                //INC Increment Memory by One, Absolute X, 3 Bytes, 7 Cycles
                case 0xFE: {
                        ChangeMemoryByOne(EAddressingMode.AbsoluteX, false);
                        IncrementCycleCount();
                        break;
                    }
                //INX Increment X Register by One, Implied, 1 Bytes, 2 Cycles
                case 0xE8: {
                        ChangeRegister(true, false);
                        break;
                    }
                //INY Increment Y Register by One, Implied, 1 Bytes, 2 Cycles
                case 0xC8: {
                        ChangeRegister(false, false);
                        break;
                    }


                // === GOTO and GOSUB ===========================================================================================================
                // ==============================================================================================================================

                //JMP Jump to New Location, Absolute 3 Bytes, 3 Cycles
                case 0x4C:
                    RegisterPC = GetAddressByAddressingMode(EAddressingMode.Absolute);
                    break;
                //JMP Jump to New Location, Indirect 3 Bytes, 5 Cycles
                case 0x6C:
                    RegisterPC = GetAddressByAddressingMode(EAddressingMode.Absolute);
                    if ((RegisterPC & 0xFF) == 0xFF) {
                        //Get the first half of the address
                        int address = ReadMemoryValue(RegisterPC);

                        //Get the second half of the address, due to the issue with page boundary it reads from the wrong location!
                        address += 256 * ReadMemoryValue(RegisterPC - 255);
                        RegisterPC = address;
                    }
                    else {
                        RegisterPC = GetAddressByAddressingMode(EAddressingMode.Absolute);
                    }
                    break;
                //JSR Jump to SubRoutine, Absolute, 3 Bytes, 6 Cycles
                case 0x20:
                    JumpToSubRoutineOperation();
                    break;
                //BRK Simulate IRQ, Implied, 1 Byte, 7 Cycles
                case 0x00:
                    BreakOperation(true, VectorIRQ);
                    break;
                //RTI Return From Interrupt, Implied, 1 Byte, 6 Cycles
                case 0x40:
                    ReturnFromInterruptOperation();
                    break;
                //RTS Return From Subroutine, Implied, 1 Byte, 6 Cycles
                case 0x60:
                    ReturnFromSubRoutineOperation();
                    break;

                // === Load Value From Memory Operations ========================================================================================
                // ==============================================================================================================================

                //LDA Load Accumulator with Memory, Immediate, 2 Bytes, 2 Cycles
                case 0xA9: {
                        RegisterA = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.Immediate));
                        SetZeroFlag(RegisterA);
                        SetNegativeFlag(RegisterA);
                        break;
                    }
                //LDA Load Accumulator with Memory, Zero Page, 2 Bytes, 3 Cycles
                case 0xA5: {
                        RegisterA = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.ZeroPage));
                        SetZeroFlag(RegisterA);
                        SetNegativeFlag(RegisterA);
                        break;
                    }
                //LDA Load Accumulator with Memory, Zero Page X, 2 Bytes, 4 Cycles
                case 0xB5: {
                        RegisterA = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.ZeroPageX));
                        SetZeroFlag(RegisterA);
                        SetNegativeFlag(RegisterA);
                        break;
                    }
                //LDA Load Accumulator with Memory, Absolute, 3 Bytes, 4 Cycles
                case 0xAD: {
                        RegisterA = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.Absolute));
                        SetZeroFlag(RegisterA);
                        SetNegativeFlag(RegisterA);
                        break;
                    }
                //LDA Load Accumulator with Memory, Absolute X, 3 Bytes, 4+ Cycles
                case 0xBD: {
                        RegisterA = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.AbsoluteX));
                        SetZeroFlag(RegisterA);
                        SetNegativeFlag(RegisterA);
                        break;
                    }
                //LDA Load Accumulator with Memory, Absolute Y, 3 Bytes, 4+ Cycles
                case 0xB9: {
                        RegisterA = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.AbsoluteY));
                        SetZeroFlag(RegisterA);
                        SetNegativeFlag(RegisterA);
                        break;
                    }
                //LDA Load Accumulator with Memory, Index Indirect, 2 Bytes, 6 Cycles
                case 0xA1: {
                        RegisterA = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.IndirectX));
                        SetZeroFlag(RegisterA);
                        SetNegativeFlag(RegisterA);
                        break;
                    }
                //LDA Load Accumulator with Memory, Indirect Index, 2 Bytes, 5+ Cycles
                case 0xB1: {
                        RegisterA = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.IndirectY));
                        SetZeroFlag(RegisterA);
                        SetNegativeFlag(RegisterA);
                        break;
                    }
                //LDX Load X with memory, Immediate, 2 Bytes, 2 Cycles
                case 0xA2: {
                        RegisterX = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.Immediate));
                        SetZeroFlag(RegisterX);
                        SetNegativeFlag(RegisterX);
                        break;
                    }
                //LDX Load X with memory, Zero Page, 2 Bytes, 3 Cycles
                case 0xA6: {
                        RegisterX = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.ZeroPage));
                        SetZeroFlag(RegisterX);
                        SetNegativeFlag(RegisterX);
                        break;
                    }
                //LDX Load X with memory, Zero Page Y, 2 Bytes, 4 Cycles
                case 0xB6: {
                        RegisterX = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.ZeroPageY));
                        SetZeroFlag(RegisterX);
                        SetNegativeFlag(RegisterX);
                        break;
                    }
                //LDX Load X with memory, Absolute, 3 Bytes, 4 Cycles
                case 0xAE: {
                        RegisterX = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.Absolute));
                        SetZeroFlag(RegisterX);
                        SetNegativeFlag(RegisterX);
                        break;
                    }
                //LDX Load X with memory, Absolute Y, 3 Bytes, 4+ Cycles
                case 0xBE: {
                        RegisterX = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.AbsoluteY));
                        SetZeroFlag(RegisterX);
                        SetNegativeFlag(RegisterX);
                        break;
                    }
                //LDY Load Y with memory, Immediate, 2 Bytes, 2 Cycles
                case 0xA0: {
                        RegisterY = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.Immediate));
                        SetZeroFlag(RegisterY);
                        SetNegativeFlag(RegisterY);
                        break;
                    }
                //LDY Load Y with memory, Zero Page, 2 Bytes, 3 Cycles
                case 0xA4: {
                        RegisterY = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.ZeroPage));
                        SetZeroFlag(RegisterY);
                        SetNegativeFlag(RegisterY);
                        break;
                    }
                //LDY Load Y with memory, Zero Page X, 2 Bytes, 4 Cycles
                case 0xB4: {
                        RegisterY = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.ZeroPageX));
                        SetZeroFlag(RegisterY);
                        SetNegativeFlag(RegisterY);
                        break;
                    }
                //LDY Load Y with memory, Absolute, 3 Bytes, 4 Cycles
                case 0xAC: {
                        RegisterY = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.Absolute));
                        SetZeroFlag(RegisterY);
                        SetNegativeFlag(RegisterY);
                        break;
                    }
                //LDY Load Y with memory, Absolue X, 3 Bytes, 4+ Cycles
                case 0xBC: {
                        RegisterY = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.AbsoluteX));
                        SetZeroFlag(RegisterY);
                        SetNegativeFlag(RegisterY);
                        break;
                    }

                // === Push/Pull Stack ==========================================================================================================
                // ==============================================================================================================================

                //PHA Push Accumulator onto Stack, Implied, 1 Byte, 3 Cycles
                case 0x48: {
                        ReadMemoryValue(RegisterPC + 1);
                        PokeStack((byte)RegisterA);
                        RegisterSP--;
                        IncrementCycleCount();
                        break;
                    }
                //PHP Push Flags onto Stack, Implied, 1 Byte, 3 Cycles
                case 0x08: {
                        ReadMemoryValue(RegisterPC + 1);

                        PushFlagsOperation();
                        RegisterSP--;
                        IncrementCycleCount();
                        break;
                    }
                //PLA Pull Accumulator from Stack, Implied, 1 Byte, 4 Cycles
                case 0x68: {
                        ReadMemoryValue(RegisterPC + 1);
                        RegisterSP++;
                        IncrementCycleCount();

                        RegisterA = PeekStack();
                        SetNegativeFlag(RegisterA);
                        SetZeroFlag(RegisterA);

                        IncrementCycleCount();
                        break;
                    }
                //PLP Pull Flags from Stack, Implied, 1 Byte, 4 Cycles
                case 0x28: {
                        ReadMemoryValue(RegisterPC + 1);

                        RegisterSP++;
                        IncrementCycleCount();

                        PullFlagsOperation();

                        IncrementCycleCount();
                        break;
                    }
                //TSX Transfer Stack Pointer to X Register, 1 Bytes, 2 Cycles
                case 0xBA: {
                        RegisterX = RegisterSP;

                        SetNegativeFlag(RegisterX);
                        SetZeroFlag(RegisterX);
                        IncrementCycleCount();
                        break;
                    }
                //TXS Transfer X Register to Stack Pointer, 1 Bytes, 2 Cycles
                case 0x9A: {
                        RegisterSP = (byte)RegisterX;
                        IncrementCycleCount();
                        break;
                    }

                // === Set Flag Operations ======================================================================================================
                // ==============================================================================================================================

                //SEC Set Carry, Implied, 1 Bytes, 2 Cycles
                case 0x38: {
                        FlagC = true;
                        IncrementCycleCount();
                        break;
                    }
                //SED Set Interrupt, Implied, 1 Bytes, 2 Cycles
                case 0xF8: {
                        FlagD = true;
                        IncrementCycleCount();
                        break;
                    }
                //SEI Set Interrupt, Implied, 1 Bytes, 2 Cycles
                case 0x78: {
                        FlagI = true;
                        IncrementCycleCount();
                        break;
                    }

                // === Shift/Rotate Operations ==================================================================================================

                // ==============================================================================================================================
                //ASL Shift Left 1 Bit Memory or Accumulator, Accumulator, 1 Bytes, 2 Cycles
                case 0x0A: {
                        AslOperation(EAddressingMode.Accumulator);
                        break;
                    }
                //ASL Shift Left 1 Bit Memory or Accumulator, Zero Page, 2 Bytes, 5 Cycles
                case 0x06: {
                        AslOperation(EAddressingMode.ZeroPage);
                        break;
                    }
                //ASL Shift Left 1 Bit Memory or Accumulator, Zero PageX, 2 Bytes, 6 Cycles
                case 0x16: {
                        AslOperation(EAddressingMode.ZeroPageX);
                        break;
                    }
                //ASL Shift Left 1 Bit Memory or Accumulator, Absolute, 3 Bytes, 6 Cycles
                case 0x0E: {
                        AslOperation(EAddressingMode.Absolute);
                        break;
                    }
                //ASL Shift Left 1 Bit Memory or Accumulator, AbsoluteX, 3 Bytes, 7 Cycles
                case 0x1E: {
                        AslOperation(EAddressingMode.AbsoluteX);
                        IncrementCycleCount();
                        break;
                    }
                //LSR Shift Left 1 Bit Memory or Accumulator, Accumulator, 1 Bytes, 2 Cycles
                case 0x4A: {
                        LsrOperation(EAddressingMode.Accumulator);
                        break;
                    }
                //LSR Shift Left 1 Bit Memory or Accumulator, Zero Page, 2 Bytes, 5 Cycles
                case 0x46: {
                        LsrOperation(EAddressingMode.ZeroPage);
                        break;
                    }
                //LSR Shift Left 1 Bit Memory or Accumulator, Zero PageX, 2 Bytes, 6 Cycles
                case 0x56: {
                        LsrOperation(EAddressingMode.ZeroPageX);
                        break;
                    }
                //LSR Shift Left 1 Bit Memory or Accumulator, Absolute, 3 Bytes, 6 Cycles
                case 0x4E: {
                        LsrOperation(EAddressingMode.Absolute);
                        break;
                    }
                //LSR Shift Left 1 Bit Memory or Accumulator, AbsoluteX, 3 Bytes, 7 Cycles
                case 0x5E: {
                        LsrOperation(EAddressingMode.AbsoluteX);
                        IncrementCycleCount();
                        break;
                    }
                //ROL Rotate Left 1 Bit Memory or Accumulator, Accumulator, 1 Bytes, 2 Cycles
                case 0x2A: {
                        RolOperation(EAddressingMode.Accumulator);
                        break;
                    }
                //ROL Rotate Left 1 Bit Memory or Accumulator, Zero Page, 2 Bytes, 5 Cycles
                case 0x26: {
                        RolOperation(EAddressingMode.ZeroPage);
                        break;
                    }
                //ROL Rotate Left 1 Bit Memory or Accumulator, Zero PageX, 2 Bytes, 6 Cycles
                case 0x36: {
                        RolOperation(EAddressingMode.ZeroPageX);
                        break;
                    }
                //ROL Rotate Left 1 Bit Memory or Accumulator, Absolute, 3 Bytes, 6 Cycles
                case 0x2E: {
                        RolOperation(EAddressingMode.Absolute);
                        break;
                    }
                //ROL Rotate Left 1 Bit Memory or Accumulator, AbsoluteX, 3 Bytes, 7 Cycles
                case 0x3E: {
                        RolOperation(EAddressingMode.AbsoluteX);
                        IncrementCycleCount();
                        break;
                    }
                //ROR Rotate Right 1 Bit Memory or Accumulator, Accumulator, 1 Bytes, 2 Cycles
                case 0x6A: {
                        RorOperation(EAddressingMode.Accumulator);
                        break;
                    }
                //ROR Rotate Right 1 Bit Memory or Accumulator, Zero Page, 2 Bytes, 5 Cycles
                case 0x66: {
                        RorOperation(EAddressingMode.ZeroPage);
                        break;
                    }
                //ROR Rotate Right 1 Bit Memory or Accumulator, Zero PageX, 2 Bytes, 6 Cycles
                case 0x76: {
                        RorOperation(EAddressingMode.ZeroPageX);
                        break;
                    }
                //ROR Rotate Right 1 Bit Memory or Accumulator, Absolute, 3 Bytes, 6 Cycles
                case 0x6E: {
                        RorOperation(EAddressingMode.Absolute);
                        break;
                    }
                //ROR Rotate Right 1 Bit Memory or Accumulator, AbsoluteX, 3 Bytes, 7 Cycles
                case 0x7E: {
                        RorOperation(EAddressingMode.AbsoluteX);
                        IncrementCycleCount();
                        break;
                    }

                // === Store in Memory ==========================================================================================================
                // ==============================================================================================================================

                //STA Store Accumulator In Memory, Zero Page, 2 Bytes, 3 Cycles
                case 0x85: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.ZeroPage), (byte)RegisterA);
                        break;
                    }
                //STA Store Accumulator In Memory, Zero Page X, 2 Bytes, 4 Cycles
                case 0x95: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.ZeroPageX), (byte)RegisterA);
                        break;
                    }
                //STA Store Accumulator In Memory, Absolute, 3 Bytes, 4 Cycles
                case 0x8D: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.Absolute), (byte)RegisterA);
                        break;
                    }
                //STA Store Accumulator In Memory, Absolute X, 3 Bytes, 5 Cycles
                case 0x9D: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.AbsoluteX), (byte)RegisterA);
                        IncrementCycleCount();
                        break;
                    }
                //STA Store Accumulator In Memory, Absolute Y, 3 Bytes, 5 Cycles
                case 0x99: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.AbsoluteY), (byte)RegisterA);
                        IncrementCycleCount();
                        break;
                    }
                //STA Store Accumulator In Memory, Indexed Indirect, 2 Bytes, 6 Cycles
                case 0x81: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.IndirectX), (byte)RegisterA);
                        break;
                    }
                //STA Store Accumulator In Memory, Indirect Indexed, 2 Bytes, 6 Cycles
                case 0x91: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.IndirectY), (byte)RegisterA);
                        IncrementCycleCount();
                        break;
                    }
                //STX Store Index X, Zero Page, 2 Bytes, 3 Cycles
                case 0x86: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.ZeroPage), (byte)RegisterX);
                        break;
                    }
                //STX Store Index X, Zero Page Y, 2 Bytes, 4 Cycles
                case 0x96: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.ZeroPageY), (byte)RegisterX);
                        break;
                    }
                //STX Store Index X, Absolute, 3 Bytes, 4 Cycles
                case 0x8E: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.Absolute), (byte)RegisterX);
                        break;
                    }
                //STY Store Index Y, Zero Page, 2 Bytes, 3 Cycles
                case 0x84: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.ZeroPage), (byte)RegisterY);
                        break;
                    }
                //STY Store Index Y, Zero Page X, 2 Bytes, 4 Cycles
                case 0x94: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.ZeroPageX), (byte)RegisterY);
                        break;
                    }
                //STY Store Index Y, Absolute, 2 Bytes, 4 Cycles
                case 0x8C: {
                        WriteMemoryValue(GetAddressByAddressingMode(EAddressingMode.Absolute), (byte)RegisterY);
                        break;
                    }

                // === Transfer between register ================================================================================================
                // ==============================================================================================================================

                //TAX Transfer Accumulator to X Register, Implied, 1 Bytes, 2 Cycles
                case 0xAA:
                    IncrementCycleCount();
                    RegisterX = RegisterA;
                    SetNegativeFlag(RegisterX);
                    SetZeroFlag(RegisterX);
                    break;
                //TAY Transfer Accumulator to Y Register, 1 Bytes, 2 Cycles
                case 0xA8:
                    IncrementCycleCount();
                    RegisterY = RegisterA;
                    SetNegativeFlag(RegisterY);
                    SetZeroFlag(RegisterY);
                    break;
                //TXA Transfer X Register to Accumulator, Implied, 1 Bytes, 2 Cycles
                case 0x8A:
                    IncrementCycleCount();
                    RegisterA = RegisterX;
                    SetNegativeFlag(RegisterA);
                    SetZeroFlag(RegisterA);
                    break;

                //TYA Transfer Y Register to Accumulator, Implied, 1 Bytes, 2 Cycles
                case 0x98:
                    IncrementCycleCount();
                    RegisterA = RegisterY;
                    SetNegativeFlag(RegisterA);
                    SetZeroFlag(RegisterA);
                    break;

                // === NOP ======================================================================================================================
                // ==============================================================================================================================

                //NOP Operation, Implied, 1 Byte, 2 Cycles
                case 0xEA:
                    IncrementCycleCount();
                    break;
                default:
                    throw new NotSupportedException(string.Format("The OpCode ${0:X2} is not supported", RegisterIR));
            }
        }

        /// <summary>
        /// Sets the IsSignNegative register
        /// </summary>
        private void SetNegativeFlag(int value) {
            //on the 6502, any value greater than 127 is negative. 128 = 1000000 in Binary. the 8th bit is set, therefore the number is a negative number.
            FlagN = value > 127;
        }

        /// <summary>
        /// Sets the IsResultZero register
        /// </summary>
        private void SetZeroFlag(int value) {
            FlagZ = value == 0;
        }

        /// <summary>
        /// Uses the AddressingMode to return the correct address based on the mode.
        /// Note: This method will not increment the program counter for any mode.
        /// Note: This method will return an error if called for either the immediate or accumulator modes. 
        /// </summary>
        /// <param name="addressingMode">The addressing Mode to use</param>
        /// <returns>The memory Location</returns>
        private int GetAddressByAddressingMode(EAddressingMode addressingMode) {
            int address;
            int highByte;
            switch (addressingMode) {
                case (EAddressingMode.Absolute): {
                        return (ReadMemoryValue(RegisterPC++) | (ReadMemoryValue(RegisterPC++) << 8));
                    }
                case EAddressingMode.AbsoluteX: {
                        //Get the low half of the address
                        address = ReadMemoryValue(RegisterPC++);

                        //Get the high byte
                        highByte = ReadMemoryValue(RegisterPC++);

                        //We crossed a page boundry, so an extra read has occurred.
                        //However, if this is an ASL, LSR, DEC, INC, ROR, ROL or STA operation, we do not decrease it by 1.
                        if (address + RegisterX > 0xFF) {
                            switch (RegisterIR) {
                                case 0x1E:
                                case 0xDE:
                                case 0xFE:
                                case 0x5E:
                                case 0x3E:
                                case 0x7E:
                                case 0x9D: {
                                        //This is a Read Fetch Write Operation, so we don't make the extra read.
                                        return ((highByte << 8 | address) + RegisterX) & 0xFFFF;
                                    }
                                default: {
                                        ReadMemoryValue((((highByte << 8 | address) + RegisterX) - 0xFF) & 0xFFFF);
                                        break;
                                    }
                            }
                        }

                        return ((highByte << 8 | address) + RegisterX) & 0xFFFF;
                    }
                case EAddressingMode.AbsoluteY: {
                        //Get the low half of the address
                        address = ReadMemoryValue(RegisterPC++);

                        //Get the high byte
                        highByte = ReadMemoryValue(RegisterPC++);

                        //We crossed a page boundry, so decrease the number of cycles by 1 if the operation is not STA
                        if (address + RegisterY > 0xFF && RegisterIR != 0x99) {
                            ReadMemoryValue((((highByte << 8 | address) + RegisterY) - 0xFF) & 0xFFFF);
                        }

                        //Bitshift the high byte into place, AND with FFFF to handle wrapping.
                        return ((highByte << 8 | address) + RegisterY) & 0xFFFF;
                    }
                case EAddressingMode.Immediate: {
                        return RegisterPC++;
                    }
                case EAddressingMode.IndirectX: {
                        //Get the location of the address to retrieve
                        address = ReadMemoryValue(RegisterPC++);
                        ReadMemoryValue(address);

                        address += RegisterX;

                        //Now get the final Address. The is not a zero page address either.
                        var finalAddress = ReadMemoryValue((address & 0xFF)) | (ReadMemoryValue((address + 1) & 0xFF) << 8);
                        return finalAddress;
                    }
                case EAddressingMode.IndirectY: {
                        address = ReadMemoryValue(RegisterPC++);

                        var finalAddress = ReadMemoryValue(address) + (ReadMemoryValue((address + 1) & 0xFF) << 8);

                        if ((finalAddress & 0xFF) + RegisterY > 0xFF && RegisterIR != 0x91) {
                            ReadMemoryValue((finalAddress + RegisterY - 0xFF) & 0xFFFF);
                        }

                        return (finalAddress + RegisterY) & 0xFFFF;
                    }
                case EAddressingMode.Relative: {
                        return RegisterPC;
                    }
                case (EAddressingMode.ZeroPage): {
                        address = ReadMemoryValue(RegisterPC++);
                        return address;
                    }
                case (EAddressingMode.ZeroPageX): {
                        address = ReadMemoryValue(RegisterPC++);
                        ReadMemoryValue(address);

                        address += RegisterX;
                        address &= 0xFF;

                        //This address wraps if its greater than 0xFF
                        if (address > 0xFF) {
                            address -= 0x100;
                            return address;
                        }

                        return address;
                    }
                case (EAddressingMode.ZeroPageY): {
                        address = ReadMemoryValue(RegisterPC++);
                        ReadMemoryValue(address);

                        address += RegisterY;
                        address &= 0xFF;

                        return address;
                    }
                default:
                    throw new InvalidOperationException(string.Format("The Address Mode '{0}' does not require an address", addressingMode));
            }
        }

        /// <summary>
        /// Moves the ProgramCounter in a given direction based on the value inputted
        /// 
        /// </summary>
        private void MoveProgramCounterByRelativeValue(byte valueToMove) {
            var movement = valueToMove > 127 ? (valueToMove - 255) : valueToMove;

            var newProgramCounter = RegisterPC + movement;

            //This makes sure that we always land on the correct spot for a positive number
            if (movement >= 0)
                newProgramCounter++;

            //We Crossed a Page Boundary. So we increment the cycle counter by one. The +1 is because we always check from the end of the instruction not the beginning
            if (((RegisterPC + 1 ^ newProgramCounter) & 0xff00) != 0x0000) {
                IncrementCycleCount();
            }

            RegisterPC = newProgramCounter;
            ReadMemoryValue(RegisterPC);
        }

        /// <summary>
        /// Returns a the value from the stack without changing the position of the stack pointer
        /// </summary>

        /// <returns>The value at the current Stack Pointer</returns>
        private byte PeekStack() {
            //The stack lives at 0x100-0x1FF, but the value is only a byte so it needs to be translated
            return ReadMemoryValueWithoutCycle(RegisterSP + 0x100);
        }

        /// <summary>
        /// Write a value directly to the stack without modifying the Stack Pointer
        /// </summary>
        /// <param name="value">The value to be written to the stack</param>
        private void PokeStack(byte value) {
            //The stack lives at 0x100-0x1FF, but the value is only a byte so it needs to be translated
            WriteMemoryValueWithoutCycle(RegisterSP + 0x100, value);
        }

        /// <summary>
        /// Coverts the Flags into its byte representation.
        /// </summary>
        /// <param name="setBreak">Determines if the break flag should be set during conversion. IRQ does not set the flag on the stack, but PHP and BRK do</param>
        /// <returns></returns>
        private byte ConvertFlagsToByte(bool setBreak) {
            return (byte)((FlagC ? 0x01 : 0) + (FlagZ ? 0x02 : 0) + (FlagI ? 0x04 : 0) +
                         (FlagD ? 8 : 0) + (setBreak ? 0x10 : 0) + 0x20 + (FlagV ? 0x40 : 0) + (FlagN ? 0x80 : 0));
        }

        [Conditional("DEBUG")]
        private void SetDisassembly() {
            if (_Logger == null || !_Logger.IsDebugEnabled) {
                return;
            }
            EAddressingMode addressMode = Utility.GetAddressingMode(RegisterIR);
            int currentProgramCounter = RegisterPC;

            currentProgramCounter = WrapProgramCounter(++currentProgramCounter);
            int? address1 = ReadMemoryValueWithoutCycle(currentProgramCounter);

            currentProgramCounter = WrapProgramCounter(++currentProgramCounter);
            int? address2 = ReadMemoryValueWithoutCycle(currentProgramCounter);

            string disassembledStep = string.Empty;

            switch (addressMode) {
                case EAddressingMode.Absolute: {
                        disassembledStep = string.Format("${0}{1}", address2.Value.ToString("X").PadLeft(2, '0'), address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case EAddressingMode.AbsoluteX: {
                        disassembledStep = string.Format("${0}{1},X", address2.Value.ToString("X").PadLeft(2, '0'), address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case EAddressingMode.AbsoluteY: {
                        disassembledStep = string.Format("${0}{1},Y", address2.Value.ToString("X").PadLeft(2, '0'), address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case EAddressingMode.Accumulator: {
                        address1 = null;
                        address2 = null;

                        disassembledStep = "A";
                        break;
                    }
                case EAddressingMode.Immediate: {
                        disassembledStep = string.Format("#${0}", address1.Value.ToString("X").PadLeft(4, '0'));
                        address2 = null;
                        break;
                    }
                case EAddressingMode.Implied: {
                        address1 = null;
                        address2 = null;
                        break;
                    }
                case EAddressingMode.Indirect: {
                        disassembledStep = string.Format("(${0}{1})", address2.Value.ToString("X").PadLeft(2, '0'), address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case EAddressingMode.IndirectX: {
                        address2 = null;

                        disassembledStep = string.Format("(${0},X)", address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case EAddressingMode.IndirectY: {
                        address2 = null;

                        disassembledStep = string.Format("(${0}),Y", address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case EAddressingMode.Relative: {
                        var valueToMove = (byte)address1.Value;

                        var movement = valueToMove > 127 ? (valueToMove - 255) : valueToMove;

                        var newProgramCounter = RegisterPC + movement;

                        //This makes sure that we always land on the correct spot for a positive number
                        if (movement >= 0)
                            newProgramCounter++;

                        var stringAddress = RegisterPC.ToString("X").PadLeft(4, '0');

                        // address1 = int.Parse(stringAddress.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                        address2 = null; // int.Parse(stringAddress.Substring(2, 2), NumberStyles.AllowHexSpecifier);

                        disassembledStep = string.Format("${0}", newProgramCounter.ToString("X").PadLeft(4, '0'));

                        break;
                    }
                case EAddressingMode.ZeroPage: {
                        address2 = null;

                        disassembledStep = string.Format("${0}", address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case EAddressingMode.ZeroPageX: {
                        address2 = null;

                        disassembledStep = string.Format("${0},X", address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case EAddressingMode.ZeroPageY: {
                        address2 = null;

                        disassembledStep = string.Format("${0},Y", address1.Value.ToString("X").PadLeft(4, '0'));
                        break;
                    }
                default:
                    throw new InvalidEnumArgumentException("Invalid Addressing Mode");

            }


            CurrentDisassembly = new Disassembly {
                HighAddress = address2.HasValue ? address2.Value.ToString("X").PadLeft(2, '0') : "  ",
                LowAddress = address1.HasValue ? address1.Value.ToString("X").PadLeft(2, '0') : "  ",
                OpCodeString = RegisterIR.ConvertOpCodeIntoString(),
                DisassemblyOutput = disassembledStep
            };

            _Logger.Debug("{0} : {1} {2} {3} {4} {5} A: {6} X: {7} Y: {8} SP {9} N: {10} V: {11} B: {12} D: {13} I: {14} Z: {15} C: {16}",
                             RegisterPC.ToString("X4"),
                             RegisterIR.ToString("X2"),
                             CurrentDisassembly.LowAddress,
                             CurrentDisassembly.HighAddress,

                             CurrentDisassembly.OpCodeString,
                             CurrentDisassembly.DisassemblyOutput.PadRight(10, ' '),

                             RegisterA.ToString("X").PadLeft(3, '0'),
                             RegisterX.ToString("X").PadLeft(3, '0'),
                             RegisterY.ToString("X").PadLeft(3, '0'),
                             RegisterSP.ToString("X").PadLeft(3, '0'),
                             Convert.ToInt16(FlagN),
                             Convert.ToInt16(FlagV),
                             0,
                             Convert.ToInt16(FlagD),
                             Convert.ToInt16(FlagI),
                             Convert.ToInt16(FlagZ),
                             Convert.ToInt16(FlagC));
        }

        private int WrapProgramCounter(int value) {
            return value & 0xFFFF;
        }

        // === Implementation of operations =============================================================================================
        // ==============================================================================================================================

        /// <summary>
        /// The ADC - Add Memory to Accumulator with Carry Operation
        /// </summary>
        /// <param name="addressingMode">The addressing mode used to perform this operation.</param>
        private void AddWithCarryOperation(EAddressingMode addressingMode) {
            //Accumulator, Carry = Accumulator + ValueInMemoryLocation + Carry 
            byte memoryValue = ReadMemoryValue(GetAddressByAddressingMode(addressingMode));
            int newValue = memoryValue + RegisterA + (FlagC ? 1 : 0);
            FlagV = (((RegisterA ^ newValue) & 0x80) != 0) && (((RegisterA ^ memoryValue) & 0x80) == 0);
            if (FlagD) {
                newValue = int.Parse(memoryValue.ToString("x")) + int.Parse(RegisterA.ToString("x")) + (FlagC ? 1 : 0);
                if (newValue > 99) {
                    FlagC = true;
                    newValue -= 100;
                }
                else {
                    FlagC = false;
                }
                newValue = (int)Convert.ToInt64(string.Concat("0x", newValue), 16);
            }
            else {
                if (newValue > 255) {
                    FlagC = true;
                    newValue -= 256;
                }
                else {
                    FlagC = false;
                }
            }
            SetZeroFlag(newValue);
            SetNegativeFlag(newValue);
            RegisterA = newValue;
        }

        /// <summary>
        /// The AND - Compare Memory with Accumulator operation
        /// </summary>
        /// <param name="addressingMode">The addressing mode being used</param>
        private void AndOperation(EAddressingMode addressingMode) {
            RegisterA = ReadMemoryValue(GetAddressByAddressingMode(addressingMode)) & RegisterA;

            SetZeroFlag(RegisterA);
            SetNegativeFlag(RegisterA);
        }

        /// <summary>
        /// The ASL - Shift Left One Bit (Memory or Accumulator)
        /// </summary>
        /// <param name="addressingMode">The addressing Mode being used</param>
        private void AslOperation(EAddressingMode addressingMode) {
            int value;
            var memoryAddress = 0;
            if (addressingMode == EAddressingMode.Accumulator) {
                ReadMemoryValue(RegisterPC + 1);
                value = RegisterA;
            }
            else {
                memoryAddress = GetAddressByAddressingMode(addressingMode);
                value = ReadMemoryValue(memoryAddress);
            }

            //Dummy Write
            if (addressingMode != EAddressingMode.Accumulator) {
                WriteMemoryValue(memoryAddress, (byte)value);
            }

            //If the 7th bit is set, then we have a carry
            FlagC = ((value & 0x80) != 0);

            //The And here ensures that if the value is greater than 255 it wraps properly.
            value = (value << 1) & 0xFE;

            SetNegativeFlag(value);
            SetZeroFlag(value);


            if (addressingMode == EAddressingMode.Accumulator)
                RegisterA = value;
            else {
                WriteMemoryValue(memoryAddress, (byte)value);
            }
        }

        /// <summary>
        /// Performs the different branch operations.
        /// </summary>
        /// <param name="performBranch">Is a branch required</param>
        private void BranchOperation(bool performBranch) {
            var value = ReadMemoryValue(GetAddressByAddressingMode(EAddressingMode.Relative));

            if (!performBranch) {
                RegisterPC++;
                return;
            }

            MoveProgramCounterByRelativeValue(value);
        }

        /// <summary>
        /// The bit operation, does an & comparison between a value in memory and the accumulator
        /// </summary>
        /// <param name="addressingMode"></param>
        private void BitOperation(EAddressingMode addressingMode) {
            var memoryValue = ReadMemoryValue(GetAddressByAddressingMode(addressingMode));
            var valueToCompare = memoryValue & RegisterA;

            FlagV = (memoryValue & 0x40) != 0;

            SetNegativeFlag(memoryValue);
            SetZeroFlag(valueToCompare);
        }

        /// <summary>
        /// The compare operation. This operation compares a value in memory with a value passed into it.
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        /// <param name="comparisonValue">The value to compare against memory</param>
        private void CompareOperation(EAddressingMode addressingMode, int comparisonValue) {
            var memoryValue = ReadMemoryValue(GetAddressByAddressingMode(addressingMode));
            var comparedValue = comparisonValue - memoryValue;

            if (comparedValue < 0)
                comparedValue += 0x10000;

            SetZeroFlag(comparedValue);

            FlagC = memoryValue <= comparisonValue;
            SetNegativeFlag(comparedValue);
        }

        /// <summary>
        /// Changes a value in memory by 1
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        /// <param name="decrement">If the operation is decrementing or incrementing the vaulue by 1 </param>
        private void ChangeMemoryByOne(EAddressingMode addressingMode, bool decrement) {
            var memoryLocation = GetAddressByAddressingMode(addressingMode);
            var memory = ReadMemoryValue(memoryLocation);

            WriteMemoryValue(memoryLocation, memory);

            if (decrement)
                memory -= 1;
            else
                memory += 1;

            SetZeroFlag(memory);
            SetNegativeFlag(memory);

            WriteMemoryValue(memoryLocation, memory);
        }

        /// <summary>
        /// Changes a value in either the X or Y register by 1
        /// </summary>
        /// <param name="useXRegister">If the operation is using the X or Y register</param>
        /// <param name="decrement">If the operation is decrementing or incrementing the vaulue by 1 </param>
        private void ChangeRegister(bool useXRegister, bool decrement) {
            var value = useXRegister ? RegisterX : RegisterY;
            if (decrement)
                value -= 1;
            else
                value += 1;
            if (value < 0x00)
                value += 0x100;
            else if (value > 0xFF)
                value -= 0x100;
            SetZeroFlag(value);
            SetNegativeFlag(value);
            IncrementCycleCount();
            if (useXRegister)
                RegisterX = value;
            else
                RegisterY = value;
        }

        /// <summary>
        /// The EOR Operation, Performs an Exclusive OR Operation against the Accumulator and a value in memory
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        private void EorOperation(EAddressingMode addressingMode) {
            RegisterA = RegisterA ^ ReadMemoryValue(GetAddressByAddressingMode(addressingMode));

            SetNegativeFlag(RegisterA);
            SetZeroFlag(RegisterA);
        }

        /// <summary>
        /// The LSR Operation. Performs a Left shift operation on a value in memory
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        private void LsrOperation(EAddressingMode addressingMode) {
            int value;
            var memoryAddress = 0;
            if (addressingMode == EAddressingMode.Accumulator) {
                ReadMemoryValue(RegisterPC + 1);
                value = RegisterA;
            }
            else {
                memoryAddress = GetAddressByAddressingMode(addressingMode);
                value = ReadMemoryValue(memoryAddress);
            }

            //Dummy Write
            if (addressingMode != EAddressingMode.Accumulator) {
                WriteMemoryValue(memoryAddress, (byte)value);
            }

            FlagN = false;

            //If the Zero bit is set, we have a carry
            FlagC = (value & 0x01) != 0;

            value = (value >> 1);

            SetZeroFlag(value);
            if (addressingMode == EAddressingMode.Accumulator)
                RegisterA = value;
            else {
                WriteMemoryValue(memoryAddress, (byte)value);
            }
        }

        /// <summary>
        /// The Or Operation. Performs an Or Operation with the accumulator and a value in memory
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        private void OrOperation(EAddressingMode addressingMode) {
            RegisterA = RegisterA | ReadMemoryValue(GetAddressByAddressingMode(addressingMode));
            SetNegativeFlag(RegisterA);
            SetZeroFlag(RegisterA);
        }

        /// <summary>
        /// The ROL operation. Performs a rotate left operation on a value in memory.
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        private void RolOperation(EAddressingMode addressingMode) {
            int value;
            var memoryAddress = 0;
            if (addressingMode == EAddressingMode.Accumulator) {
                //Dummy Read
                ReadMemoryValue(RegisterPC + 1);
                value = RegisterA;
            }
            else {
                memoryAddress = GetAddressByAddressingMode(addressingMode);
                value = ReadMemoryValue(memoryAddress);
            }

            //Dummy Write
            if (addressingMode != EAddressingMode.Accumulator) {
                WriteMemoryValue(memoryAddress, (byte)value);
            }

            //Store the carry flag before shifting it
            var newCarry = (0x80 & value) != 0;

            //The And here ensures that if the value is greater than 255 it wraps properly.
            value = (value << 1) & 0xFE;

            if (FlagC)
                value = value | 0x01;

            FlagC = newCarry;

            SetZeroFlag(value);
            SetNegativeFlag(value);


            if (addressingMode == EAddressingMode.Accumulator)
                RegisterA = value;
            else {
                WriteMemoryValue(memoryAddress, (byte)value);
            }
        }

        /// <summary>
        /// The ROR operation. Performs a rotate right operation on a value in memory.
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        private void RorOperation(EAddressingMode addressingMode) {
            int value;
            var memoryAddress = 0;
            if (addressingMode == EAddressingMode.Accumulator) {
                //Dummy Read
                ReadMemoryValue(RegisterPC + 1);
                value = RegisterA;
            }
            else {
                memoryAddress = GetAddressByAddressingMode(addressingMode);
                value = ReadMemoryValue(memoryAddress);
            }

            //Dummy Write
            if (addressingMode != EAddressingMode.Accumulator) {
                WriteMemoryValue(memoryAddress, (byte)value);
            }

            //Store the carry flag before shifting it
            var newCarry = (0x01 & value) != 0;

            value = (value >> 1);

            //If the carry flag is set then 0x
            if (FlagC)
                value = value | 0x80;

            FlagC = newCarry;

            SetZeroFlag(value);
            SetNegativeFlag(value);

            if (addressingMode == EAddressingMode.Accumulator)
                RegisterA = value;
            else {
                WriteMemoryValue(memoryAddress, (byte)value);
            }
        }

        /// <summary>
        /// The SBC operation. Performs a subtract with carry operation on the accumulator and a value in memory.
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        private void SubtractWithBorrowOperation(EAddressingMode addressingMode) {
            byte memoryValue = ReadMemoryValue(GetAddressByAddressingMode(addressingMode));
            int newValue = FlagD
                               ? int.Parse(RegisterA.ToString("x")) - int.Parse(memoryValue.ToString("x")) - (FlagC ? 0 : 1)
                               : RegisterA - memoryValue - (FlagC ? 0 : 1);

            FlagC = newValue >= 0;

            if (FlagD) {
                if (newValue < 0) {
                    newValue += 100;
                }
                newValue = (int)Convert.ToInt64(string.Concat("0x", newValue), 16);
            }
            else {
                FlagV = (((RegisterA ^ newValue) & 0x80) != 0) && (((RegisterA ^ memoryValue) & 0x80) != 0);
                if (newValue < 0) {
                    newValue += 256;
                }
            }

            SetNegativeFlag(newValue);
            SetZeroFlag(newValue);

            RegisterA = newValue;
        }

        /// <summary>
        /// The PSP Operation. Pushes the Status Flags to the stack
        /// </summary>
        private void PushFlagsOperation() {
            PokeStack(ConvertFlagsToByte(true));
        }

        /// <summary>
        /// The PLP Operation. Pull the status flags off the stack on sets the flags accordingly.
        /// </summary>
        private void PullFlagsOperation() {
            var flags = PeekStack();
            FlagC = (flags & 0x01) != 0;
            FlagZ = (flags & 0x02) != 0;
            FlagI = (flags & 0x04) != 0;
            FlagD = (flags & 0x08) != 0;
            FlagV = (flags & 0x40) != 0;
            FlagN = (flags & 0x80) != 0;
        }

        /// <summary>
        /// The JSR routine. Jumps to a subroutine. 
        /// </summary>
        private void JumpToSubRoutineOperation() {
            IncrementCycleCount();
            //Put the high value on the stack, this should be the address after our operation -1
            //The RTS operation increments the PC by 1 which is why we don't move 2
            PokeStack((byte)(((RegisterPC + 1) >> 8) & 0xFF));
            RegisterSP--;
            IncrementCycleCount();

            PokeStack((byte)((RegisterPC + 1) & 0xFF));
            RegisterSP--;
            IncrementCycleCount();

            RegisterPC = GetAddressByAddressingMode(EAddressingMode.Absolute);
        }

        /// <summary>
        /// The RTS routine. Called when returning from a subroutine.
        /// </summary>
        private void ReturnFromSubRoutineOperation() {
            ReadMemoryValue(++RegisterPC);
            RegisterSP++;
            IncrementCycleCount();

            var lowBit = PeekStack();
            RegisterSP++;
            IncrementCycleCount();

            var highBit = PeekStack() << 8;
            IncrementCycleCount();

            RegisterPC = (highBit | lowBit) + 1;
            IncrementCycleCount();
        }


        /// <summary>
        /// The BRK routine. Called when a BRK occurs.
        /// </summary>
        private void BreakOperation(bool isBrk, int vector) {
            ReadMemoryValue(++RegisterPC);
            //Put the high value on the stack
            //When we RTI the address will be incremented by one, and the address after a break will not be used.
            PokeStack((byte)(((RegisterPC) >> 8) & 0xFF));
            RegisterSP--;
            IncrementCycleCount();

            //Put the low value on the stack
            PokeStack((byte)((RegisterPC) & 0xFF));
            RegisterSP--;
            IncrementCycleCount();

            //We only set the Break Flag is a Break Occurs
            if (isBrk) {
                PokeStack((byte)(ConvertFlagsToByte(true) | 0x10));
            }
            else {
                PokeStack(ConvertFlagsToByte(false));
            }
            RegisterSP--;
            IncrementCycleCount();

            FlagI = true;

            RegisterPC = (ReadMemoryValue(vector + 1) << 8) | ReadMemoryValue(vector);
        }

        /// <summary>
        /// The RTI routine. Called when returning from a BRK opertion.
        /// Note: when called after a BRK operation the Program Counter is not set to the location after the BRK,
        /// it is set +1
        /// </summary>
        private void ReturnFromInterruptOperation() {
            ReadMemoryValue(++RegisterPC);
            RegisterSP++;
            IncrementCycleCount();

            PullFlagsOperation();
            RegisterSP++;
            IncrementCycleCount();

            var lowBit = PeekStack();
            RegisterSP++;
            IncrementCycleCount();

            var highBit = PeekStack() << 8;
            IncrementCycleCount();

            RegisterPC = (highBit | lowBit);
        }

        /// <summary>
        /// This is ran anytime an NMI occurrs
        /// </summary>
	    private void ProcessNMI() {
            RegisterPC--;
            BreakOperation(false, VectorNMI);
            RegisterIR = ReadMemoryValue(RegisterPC);
            SetDisassembly();
            _PendingNMI = false;
        }

        /// <summary>
        /// This is called when PendingIRQ is true.
        /// </summary>
        private void ProcessIRQ() {
            RegisterPC--;
            BreakOperation(false, VectorIRQ);
            RegisterIR = ReadMemoryValue(RegisterPC);
            SetDisassembly();
        }
    }
}