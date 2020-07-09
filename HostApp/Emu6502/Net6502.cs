using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace HostApp.Emu6502 {
    /// <summary>
    /// An Implementation of a 6502 Processor, as implemented at aaronmell/6502Net
    /// </summary>
    internal class Net6502 {

        public const int VectorNMI = 0xFFFA;
        public const int VectorRESET = 0xFFFC;
        public const int VectorIRQ = 0xFFFE;

        public int CycleCount;

        public override string ToString() => $"Net6502: {CycleCount} cycles";

        private readonly HostLogger _Logger;
        private int _PC;
        private int _SP;
        private bool _PreviousInterrupt;
        private bool _Interrupt;

        // === Properties ============================================================================================
        // Note from aaronmell: all properties are public to facilitate ease of debugging/testing.
        // ===========================================================================================================

        /// <summary>
        /// The Accumulator. This value is implemented as an integer intead of a byte.
        /// This is done so we can detect wrapping of the value and set the correct number of cycles.
        /// </summary>
        public int A { get; protected set; }

        /// <summary>
        /// The X Index Register
        /// </summary>
        public int X { get; private set; }

        /// <summary>
        /// The Y Index Register
        /// </summary>
        public int Y { get; private set; }

        /// <summary>
        /// The instruction register - current Op Code being executed by the system
        /// </summary>
        public int IR { get; private set; }

        /// <summary>
        /// The disassembly of the current operation. This value is only set when the CPU is built in debug mode.
        /// </summary>
        public Disassembly CurrentDisassembly { get; private set; }
        /// <summary>
        /// Points to the Current Address of the instruction being executed by the system. 
        /// The PC wraps when the value is greater than 65535, or less than 0. 
        /// </summary>
        public int PC {
            get { return _PC; }
            private set { _PC = WrapProgramCounter(value); }
        }
        /// <summary>
        /// Points to the Current Position of the Stack.
        /// This value is a 00-FF value but is offset to point to the location in memory where the stack resides.
        /// </summary>
        public int SP {
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
        /// An external action that occurs when the cycle count is incremented
        /// </summary>
        public Action OnCycle { get; set; }

        /// <summary>
        /// This is the carry flag. when adding, if the result is greater than 255 or 99 in BCD Mode, then this bit is enabled. 
        /// In subtraction this is reversed and set to false if a borrow is required IE the result is less than 0
        /// </summary>
        public bool FlagC { get; protected set; }

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
        public bool FlagV { get; protected set; }
        /// <summary>
        /// Set to true if the result of an operation is negative in ADC and SBC operations. 
        /// Remember that 128-256 represent negative numbers when doing signed math.
        /// In shift operations the sign holds the carry.
        /// </summary>
        public bool FlagN { get; private set; }

        /// <summary>
        /// Set to true when an NMI should occur
        /// </summary>
        public bool TriggerNmi { get; set; }

        /// Set to true when an IRQ has occurred and is being processed by the CPU
        public bool TriggerIRQ { get; private set; }

        // === Public Methods ========================================================================================
        // ===========================================================================================================
        
        /// <summary>
        /// Default Constructor, Instantiates a new instance of the processor.
        /// </summary>
        public Net6502(HostLogger logger = null, Action onCycle = null) {
            _Logger = logger;
            SP = 0x100;
            OnCycle = onCycle;
        }

        /// <summary>
        /// Initializes the processor to its default state.
        /// </summary>
        public void Reset() {
            ResetCycleCount();
            SP = 0x1FD;
            //Set the Program Counter to the Reset Vector Address.
            PC = 0xFFFC;
            //Reset the Program Counter to the Address contained in the Reset Vector
            PC = ReadMemoryValueWithoutCycle(PC) | (ReadMemoryValueWithoutCycle(PC + 1) << 8);
            IR = ReadMemoryValueWithoutCycle(PC);
            //SetDisassembly();
            FlagI = true;
            _PreviousInterrupt = false;
            TriggerNmi = false;
            TriggerIRQ = false;
        }

        /// <summary>
        /// Performs the next step on the processor
        /// </summary>
        public void NextStep() {

            IR = ReadMemoryValue(PC);

            SetDisassembly();

            PC++;

            ExecuteOpCode();

            if (_PreviousInterrupt) {
                if (TriggerNmi) {
                    ProcessNMI();
                    TriggerNmi = false;
                }
                else if (TriggerIRQ) {
                    ProcessIRQ();
                    TriggerIRQ = false;
                }
            }
        }

        /// <summary>
        /// The InterruptRequest or IRQ
        /// </summary>
        public void InterruptRequest() {
            TriggerIRQ = true;
        }

        /// <summary>
        /// Returns the byte at the given address.
        /// </summary>
        /// <param name="address">The address to return</param>
        /// <returns>the byte being returned</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual byte ReadMemoryValue(int address) {
            byte value = 0xEA;
            IncrementCycleCount();
            return value;
        }

        /// <summary>
        /// Returns the byte at a given address without incrementing the cycle. Useful for test harness. 
        /// </summary>
        public virtual byte ReadMemoryValueWithoutCycle(int address) {
            byte value = 0xEA;
            return value;
        }

        /// <summary>
        /// Writes data to the given address.
        /// </summary>
        /// <param name="address">The address to write data to</param>
        /// <param name="data">The data to write</param>
        public virtual void WriteMemoryValue(int address, byte data) {
            IncrementCycleCount();
        }

        public virtual void WriteMemoryValueWithoutCycle(int address, byte data) {

        }

        /// <summary>
        /// Gets the Number of Cycles that have elapsed
        /// </summary>
        /// <returns>The number of elapsed cycles</returns>
	    public int GetCycleCount() {
            return CycleCount;
        }

        /// <summary>
        /// Increments the Cycle Count, causes a CycleCountIncrementedAction to fire.
        /// </summary>
        protected void IncrementCycleCount() {
            CycleCount++;
            OnCycle?.Invoke();

            _PreviousInterrupt = _Interrupt;
            _Interrupt = TriggerNmi || (TriggerIRQ && !FlagI);
        }

        /// <summary>
        /// Resets the Cycle Count back to 0
        /// </summary>
	    public void ResetCycleCount() {
            CycleCount = 0;
        }

        // === Private Methods =======================================================================================
        // ===========================================================================================================

        /// <summary>
        /// Executes an Opcode
        /// </summary>
        private void ExecuteOpCode() {
            //The x+ cycles denotes that if a page wrap occurs, then an additional cycle is consumed.
            //The x++ cycles denotes that 1 cycle is added when a branch occurs and it on the same page, and two cycles are added if its on a different page./
            //This is handled inside the GetValueFromMemory Method
            switch (IR) {
                // --- Add / Subtract Operations ---------------------------------------------------------------------
                //ADC Add With Carry, Indexed Indirect, 2 Bytes, 6 Cycles
                case 0x61:
                    AddWithCarryOperation(AddressingMode.IndirectX);
                    break;
                //ADC Add With Carry, Zero Page, 2 Bytes, 3 Cycles
                case 0x65:
                    AddWithCarryOperation(AddressingMode.ZeroPage);
                    break;
                //ADC Add With Carry, Immediate, 2 Bytes, 2 Cycles
                case 0x69:
                    AddWithCarryOperation(AddressingMode.Immediate);
                    break;
                //ADC Add With Carry, Absolute, 3 Bytes, 4 Cycles
                case 0x6D:
                    AddWithCarryOperation(AddressingMode.Absolute);
                    break;
                //ADC Add With Carry, Indexed Indirect, 2 Bytes, 5+ Cycles
                case 0x71:
                    AddWithCarryOperation(AddressingMode.IndirectY);
                    break;
                //ADC Add With Carry, Zero Page X, 2 Bytes, 4 Cycles
                case 0x75:
                    AddWithCarryOperation(AddressingMode.ZeroPageX);
                    break;
                //ADC Add With Carry, Absolute Y, 3 Bytes, 4+ Cycles
                case 0x79:
                    AddWithCarryOperation(AddressingMode.AbsoluteY);
                    break;
                //ADC Add With Carry, Absolute X, 3 Bytes, 4+ Cycles
                case 0x7D:
                    AddWithCarryOperation(AddressingMode.AbsoluteX);
                    break;
                //SBC Subtract with Borrow, Immediate, 2 Bytes, 2 Cycles
                case 0xE9: {
                        SubtractWithBorrowOperation(AddressingMode.Immediate);
                        break;
                    }
                //SBC Subtract with Borrow, Zero Page, 2 Bytes, 3 Cycles
                case 0xE5: {
                        SubtractWithBorrowOperation(AddressingMode.ZeroPage);
                        break;
                    }
                //SBC Subtract with Borrow, Zero Page X, 2 Bytes, 4 Cycles
                case 0xF5: {
                        SubtractWithBorrowOperation(AddressingMode.ZeroPageX);
                        break;
                    }
                //SBC Subtract with Borrow, Absolute, 3 Bytes, 4 Cycles
                case 0xED: {
                        SubtractWithBorrowOperation(AddressingMode.Absolute);
                        break;
                    }
                //SBC Subtract with Borrow, Absolute X, 3 Bytes, 4+ Cycles
                case 0xFD: {
                        SubtractWithBorrowOperation(AddressingMode.AbsoluteX);
                        break;
                    }
                //SBC Subtract with Borrow, Absolute Y, 3 Bytes, 4+ Cycles
                case 0xF9: {
                        SubtractWithBorrowOperation(AddressingMode.AbsoluteY);
                        break;
                    }
                //SBC Subtract with Borrow, Indexed Indirect, 2 Bytes, 6 Cycles
                case 0xE1: {
                        SubtractWithBorrowOperation(AddressingMode.IndirectX);
                        break;
                    }
                //SBC Subtract with Borrow, Indexed Indirect, 2 Bytes, 5+ Cycles
                case 0xF1: {
                        SubtractWithBorrowOperation(AddressingMode.IndirectY);
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
                        AndOperation(AddressingMode.Immediate);
                        break;
                    }
                //AND Compare Memory with Accumulator, Zero Page, 2 Bytes, 3 Cycles
                case 0x25: {
                        AndOperation(AddressingMode.ZeroPage);
                        break;
                    }
                //AND Compare Memory with Accumulator, Zero PageX, 2 Bytes, 3 Cycles
                case 0x35: {
                        AndOperation(AddressingMode.ZeroPageX);
                        break;
                    }
                //AND Compare Memory with Accumulator, Absolute,  3 Bytes, 4 Cycles
                case 0x2D: {
                        AndOperation(AddressingMode.Absolute);
                        break;
                    }
                //AND Compare Memory with Accumulator, AbsolueteX 3 Bytes, 4+ Cycles
                case 0x3D: {
                        AndOperation(AddressingMode.AbsoluteX);
                        break;
                    }
                //AND Compare Memory with Accumulator, AbsoluteY, 3 Bytes, 4+ Cycles
                case 0x39: {
                        AndOperation(AddressingMode.AbsoluteY);
                        break;
                    }
                //AND Compare Memory with Accumulator, IndexedIndirect, 2 Bytes, 6 Cycles
                case 0x21: {
                        AndOperation(AddressingMode.IndirectX);
                        break;
                    }
                //AND Compare Memory with Accumulator, IndirectIndexed, 2 Bytes, 5 Cycles
                case 0x31: {
                        AndOperation(AddressingMode.IndirectY);
                        break;
                    }
                //BIT Compare Memory with Accumulator, Zero Page, 2 Bytes, 3 Cycles
                case 0x24: {
                        BitOperation(AddressingMode.ZeroPage);
                        break;
                    }
                //BIT Compare Memory with Accumulator, Absolute, 2 Bytes, 4 Cycles
                case 0x2C: {
                        BitOperation(AddressingMode.Absolute);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, Immediate, 2 Bytes, 2 Cycles
                case 0x49: {
                        EorOperation(AddressingMode.Immediate);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, Zero Page, 2 Bytes, 3 Cycles
                case 0x45: {
                        EorOperation(AddressingMode.ZeroPage);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, Zero Page X, 2 Bytes, 4 Cycles
                case 0x55: {
                        EorOperation(AddressingMode.ZeroPageX);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, Absolute, 3 Bytes, 4 Cycles
                case 0x4D: {
                        EorOperation(AddressingMode.Absolute);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, Absolute X, 3 Bytes, 4+ Cycles
                case 0x5D: {
                        EorOperation(AddressingMode.AbsoluteX);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, Absolute Y, 3 Bytes, 4+ Cycles
                case 0x59: {
                        EorOperation(AddressingMode.AbsoluteY);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, IndexedIndirect, 2 Bytes 6 Cycles
                case 0x41: {
                        EorOperation(AddressingMode.IndirectX);
                        break;
                    }
                //EOR Exclusive OR Memory with Accumulator, IndirectIndexed, 2 Bytes 5 Cycles
                case 0x51: {
                        EorOperation(AddressingMode.IndirectY);
                        break;
                    }
                //ORA Compare Memory with Accumulator, Immediate, 2 Bytes, 2 Cycles
                case 0x09: {
                        OrOperation(AddressingMode.Immediate);
                        break;
                    }
                //ORA Compare Memory with Accumulator, Zero Page, 2 Bytes, 2 Cycles
                case 0x05: {
                        OrOperation(AddressingMode.ZeroPage);
                        break;
                    }
                //ORA Compare Memory with Accumulator, Zero PageX, 2 Bytes, 4 Cycles
                case 0x15: {
                        OrOperation(AddressingMode.ZeroPageX);
                        break;
                    }
                //ORA Compare Memory with Accumulator, Absolute,  3 Bytes, 4 Cycles
                case 0x0D: {
                        OrOperation(AddressingMode.Absolute);
                        break;
                    }
                //ORA Compare Memory with Accumulator, AbsolueteX 3 Bytes, 4+ Cycles
                case 0x1D: {
                        OrOperation(AddressingMode.AbsoluteX);
                        break;
                    }
                //ORA Compare Memory with Accumulator, AbsoluteY, 3 Bytes, 4+ Cycles
                case 0x19: {
                        OrOperation(AddressingMode.AbsoluteY);
                        break;
                    }
                //ORA Compare Memory with Accumulator, IndexedIndirect, 2 Bytes, 6 Cycles
                case 0x01: {
                        OrOperation(AddressingMode.IndirectX);
                        break;
                    }
                //ORA Compare Memory with Accumulator, IndirectIndexed, 2 Bytes, 5 Cycles
                case 0x11: {
                        OrOperation(AddressingMode.IndirectY);
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
                        CompareOperation(AddressingMode.Immediate, A);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Zero Page, 2 Bytes, 3 Cycles
                case 0xC5: {
                        CompareOperation(AddressingMode.ZeroPage, A);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Zero Page x, 2 Bytes, 4 Cycles
                case 0xD5: {
                        CompareOperation(AddressingMode.ZeroPageX, A);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Absolute, 3 Bytes, 4 Cycles
                case 0xCD: {
                        CompareOperation(AddressingMode.Absolute, A);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Absolute X, 2 Bytes, 4 Cycles
                case 0xDD: {
                        CompareOperation(AddressingMode.AbsoluteX, A);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Absolute Y, 2 Bytes, 4 Cycles
                case 0xD9: {
                        CompareOperation(AddressingMode.AbsoluteY, A);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Indirect X, 2 Bytes, 6 Cycles
                case 0xC1: {
                        CompareOperation(AddressingMode.IndirectX, A);
                        break;
                    }
                //CMP Compare Accumulator with Memory, Indirect Y, 2 Bytes, 5 Cycles
                case 0xD1: {
                        CompareOperation(AddressingMode.IndirectY, A);
                        break;
                    }
                //CPX Compare Accumulator with X Register, Immediate, 2 Bytes, 2 Cycles
                case 0xE0: {
                        CompareOperation(AddressingMode.Immediate, X);
                        break;
                    }
                //CPX Compare Accumulator with X Register, Zero Page, 2 Bytes, 3 Cycles
                case 0xE4: {
                        CompareOperation(AddressingMode.ZeroPage, X);
                        break;
                    }
                //CPX Compare Accumulator with X Register, Absolute, 3 Bytes, 4 Cycles
                case 0xEC: {
                        CompareOperation(AddressingMode.Absolute, X);
                        break;
                    }
                //CPY Compare Accumulator with Y Register, Immediate, 2 Bytes, 2 Cycles
                case 0xC0: {
                        CompareOperation(AddressingMode.Immediate, Y);
                        break;
                    }
                //CPY Compare Accumulator with Y Register, Zero Page, 2 Bytes, 3 Cycles
                case 0xC4: {
                        CompareOperation(AddressingMode.ZeroPage, Y);
                        break;
                    }
                //CPY Compare Accumulator with Y Register, Absolute, 3 Bytes, 4 Cycles
                case 0xCC: {
                        CompareOperation(AddressingMode.Absolute, Y);
                        break;
                    }

                // === Increment/Decrement Operations ===========================================================================================
                // ==============================================================================================================================

                //DEC Decrement Memory by One, Zero Page, 2 Bytes, 5 Cycles
                case 0xC6: {
                        ChangeMemoryByOne(AddressingMode.ZeroPage, true);
                        break;
                    }
                //DEC Decrement Memory by One, Zero Page X, 2 Bytes, 6 Cycles
                case 0xD6: {
                        ChangeMemoryByOne(AddressingMode.ZeroPageX, true);
                        break;
                    }
                //DEC Decrement Memory by One, Absolute, 3 Bytes, 6 Cycles
                case 0xCE: {
                        ChangeMemoryByOne(AddressingMode.Absolute, true);
                        break;
                    }
                //DEC Decrement Memory by One, Absolute X, 3 Bytes, 7 Cycles
                case 0xDE: {
                        ChangeMemoryByOne(AddressingMode.AbsoluteX, true);
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
                        ChangeMemoryByOne(AddressingMode.ZeroPage, false);
                        break;
                    }
                //INC Increment Memory by One, Zero Page X, 2 Bytes, 6 Cycles
                case 0xF6: {
                        ChangeMemoryByOne(AddressingMode.ZeroPageX, false);
                        break;
                    }
                //INC Increment Memory by One, Absolute, 3 Bytes, 6 Cycles
                case 0xEE: {
                        ChangeMemoryByOne(AddressingMode.Absolute, false);
                        break;
                    }
                //INC Increment Memory by One, Absolute X, 3 Bytes, 7 Cycles
                case 0xFE: {
                        ChangeMemoryByOne(AddressingMode.AbsoluteX, false);
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
                    PC = GetAddressByAddressingMode(AddressingMode.Absolute);
                    break;
                //JMP Jump to New Location, Indirect 3 Bytes, 5 Cycles
                case 0x6C:
                    PC = GetAddressByAddressingMode(AddressingMode.Absolute);
                    if ((PC & 0xFF) == 0xFF) {
                        //Get the first half of the address
                        int address = ReadMemoryValue(PC);

                        //Get the second half of the address, due to the issue with page boundary it reads from the wrong location!
                        address += 256 * ReadMemoryValue(PC - 255);
                        PC = address;
                    }
                    else {
                        PC = GetAddressByAddressingMode(AddressingMode.Absolute);
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
                        A = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.Immediate));
                        SetZeroFlag(A);
                        SetNegativeFlag(A);
                        break;
                    }
                //LDA Load Accumulator with Memory, Zero Page, 2 Bytes, 3 Cycles
                case 0xA5: {
                        A = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.ZeroPage));
                        SetZeroFlag(A);
                        SetNegativeFlag(A);
                        break;
                    }
                //LDA Load Accumulator with Memory, Zero Page X, 2 Bytes, 4 Cycles
                case 0xB5: {
                        A = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.ZeroPageX));
                        SetZeroFlag(A);
                        SetNegativeFlag(A);
                        break;
                    }
                //LDA Load Accumulator with Memory, Absolute, 3 Bytes, 4 Cycles
                case 0xAD: {
                        A = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.Absolute));
                        SetZeroFlag(A);
                        SetNegativeFlag(A);
                        break;
                    }
                //LDA Load Accumulator with Memory, Absolute X, 3 Bytes, 4+ Cycles
                case 0xBD: {
                        A = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.AbsoluteX));
                        SetZeroFlag(A);
                        SetNegativeFlag(A);
                        break;
                    }
                //LDA Load Accumulator with Memory, Absolute Y, 3 Bytes, 4+ Cycles
                case 0xB9: {
                        A = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.AbsoluteY));
                        SetZeroFlag(A);
                        SetNegativeFlag(A);
                        break;
                    }
                //LDA Load Accumulator with Memory, Index Indirect, 2 Bytes, 6 Cycles
                case 0xA1: {
                        A = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.IndirectX));
                        SetZeroFlag(A);
                        SetNegativeFlag(A);
                        break;
                    }
                //LDA Load Accumulator with Memory, Indirect Index, 2 Bytes, 5+ Cycles
                case 0xB1: {
                        A = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.IndirectY));
                        SetZeroFlag(A);
                        SetNegativeFlag(A);
                        break;
                    }
                //LDX Load X with memory, Immediate, 2 Bytes, 2 Cycles
                case 0xA2: {
                        X = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.Immediate));
                        SetZeroFlag(X);
                        SetNegativeFlag(X);
                        break;
                    }
                //LDX Load X with memory, Zero Page, 2 Bytes, 3 Cycles
                case 0xA6: {
                        X = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.ZeroPage));
                        SetZeroFlag(X);
                        SetNegativeFlag(X);
                        break;
                    }
                //LDX Load X with memory, Zero Page Y, 2 Bytes, 4 Cycles
                case 0xB6: {
                        X = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.ZeroPageY));
                        SetZeroFlag(X);
                        SetNegativeFlag(X);
                        break;
                    }
                //LDX Load X with memory, Absolute, 3 Bytes, 4 Cycles
                case 0xAE: {
                        X = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.Absolute));
                        SetZeroFlag(X);
                        SetNegativeFlag(X);
                        break;
                    }
                //LDX Load X with memory, Absolute Y, 3 Bytes, 4+ Cycles
                case 0xBE: {
                        X = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.AbsoluteY));
                        SetZeroFlag(X);
                        SetNegativeFlag(X);
                        break;
                    }
                //LDY Load Y with memory, Immediate, 2 Bytes, 2 Cycles
                case 0xA0: {
                        Y = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.Immediate));
                        SetZeroFlag(Y);
                        SetNegativeFlag(Y);
                        break;
                    }
                //LDY Load Y with memory, Zero Page, 2 Bytes, 3 Cycles
                case 0xA4: {
                        Y = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.ZeroPage));
                        SetZeroFlag(Y);
                        SetNegativeFlag(Y);
                        break;
                    }
                //LDY Load Y with memory, Zero Page X, 2 Bytes, 4 Cycles
                case 0xB4: {
                        Y = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.ZeroPageX));
                        SetZeroFlag(Y);
                        SetNegativeFlag(Y);
                        break;
                    }
                //LDY Load Y with memory, Absolute, 3 Bytes, 4 Cycles
                case 0xAC: {
                        Y = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.Absolute));
                        SetZeroFlag(Y);
                        SetNegativeFlag(Y);
                        break;
                    }
                //LDY Load Y with memory, Absolue X, 3 Bytes, 4+ Cycles
                case 0xBC: {
                        Y = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.AbsoluteX));
                        SetZeroFlag(Y);
                        SetNegativeFlag(Y);
                        break;
                    }

                // === Push/Pull Stack ==========================================================================================================
                // ==============================================================================================================================

                //PHA Push Accumulator onto Stack, Implied, 1 Byte, 3 Cycles
                case 0x48: {
                        ReadMemoryValue(PC + 1);
                        PokeStack((byte)A);
                        SP--;
                        IncrementCycleCount();
                        break;
                    }
                //PHP Push Flags onto Stack, Implied, 1 Byte, 3 Cycles
                case 0x08: {
                        ReadMemoryValue(PC + 1);

                        PushFlagsOperation();
                        SP--;
                        IncrementCycleCount();
                        break;
                    }
                //PLA Pull Accumulator from Stack, Implied, 1 Byte, 4 Cycles
                case 0x68: {
                        ReadMemoryValue(PC + 1);
                        SP++;
                        IncrementCycleCount();

                        A = PeekStack();
                        SetNegativeFlag(A);
                        SetZeroFlag(A);

                        IncrementCycleCount();
                        break;
                    }
                //PLP Pull Flags from Stack, Implied, 1 Byte, 4 Cycles
                case 0x28: {
                        ReadMemoryValue(PC + 1);

                        SP++;
                        IncrementCycleCount();

                        PullFlagsOperation();

                        IncrementCycleCount();
                        break;
                    }
                //TSX Transfer Stack Pointer to X Register, 1 Bytes, 2 Cycles
                case 0xBA: {
                        X = SP;

                        SetNegativeFlag(X);
                        SetZeroFlag(X);
                        IncrementCycleCount();
                        break;
                    }
                //TXS Transfer X Register to Stack Pointer, 1 Bytes, 2 Cycles
                case 0x9A: {
                        SP = (byte)X;
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
                        AslOperation(AddressingMode.Accumulator);
                        break;
                    }
                //ASL Shift Left 1 Bit Memory or Accumulator, Zero Page, 2 Bytes, 5 Cycles
                case 0x06: {
                        AslOperation(AddressingMode.ZeroPage);
                        break;
                    }
                //ASL Shift Left 1 Bit Memory or Accumulator, Zero PageX, 2 Bytes, 6 Cycles
                case 0x16: {
                        AslOperation(AddressingMode.ZeroPageX);
                        break;
                    }
                //ASL Shift Left 1 Bit Memory or Accumulator, Absolute, 3 Bytes, 6 Cycles
                case 0x0E: {
                        AslOperation(AddressingMode.Absolute);
                        break;
                    }
                //ASL Shift Left 1 Bit Memory or Accumulator, AbsoluteX, 3 Bytes, 7 Cycles
                case 0x1E: {
                        AslOperation(AddressingMode.AbsoluteX);
                        IncrementCycleCount();
                        break;
                    }
                //LSR Shift Left 1 Bit Memory or Accumulator, Accumulator, 1 Bytes, 2 Cycles
                case 0x4A: {
                        LsrOperation(AddressingMode.Accumulator);
                        break;
                    }
                //LSR Shift Left 1 Bit Memory or Accumulator, Zero Page, 2 Bytes, 5 Cycles
                case 0x46: {
                        LsrOperation(AddressingMode.ZeroPage);
                        break;
                    }
                //LSR Shift Left 1 Bit Memory or Accumulator, Zero PageX, 2 Bytes, 6 Cycles
                case 0x56: {
                        LsrOperation(AddressingMode.ZeroPageX);
                        break;
                    }
                //LSR Shift Left 1 Bit Memory or Accumulator, Absolute, 3 Bytes, 6 Cycles
                case 0x4E: {
                        LsrOperation(AddressingMode.Absolute);
                        break;
                    }
                //LSR Shift Left 1 Bit Memory or Accumulator, AbsoluteX, 3 Bytes, 7 Cycles
                case 0x5E: {
                        LsrOperation(AddressingMode.AbsoluteX);
                        IncrementCycleCount();
                        break;
                    }
                //ROL Rotate Left 1 Bit Memory or Accumulator, Accumulator, 1 Bytes, 2 Cycles
                case 0x2A: {
                        RolOperation(AddressingMode.Accumulator);
                        break;
                    }
                //ROL Rotate Left 1 Bit Memory or Accumulator, Zero Page, 2 Bytes, 5 Cycles
                case 0x26: {
                        RolOperation(AddressingMode.ZeroPage);
                        break;
                    }
                //ROL Rotate Left 1 Bit Memory or Accumulator, Zero PageX, 2 Bytes, 6 Cycles
                case 0x36: {
                        RolOperation(AddressingMode.ZeroPageX);
                        break;
                    }
                //ROL Rotate Left 1 Bit Memory or Accumulator, Absolute, 3 Bytes, 6 Cycles
                case 0x2E: {
                        RolOperation(AddressingMode.Absolute);
                        break;
                    }
                //ROL Rotate Left 1 Bit Memory or Accumulator, AbsoluteX, 3 Bytes, 7 Cycles
                case 0x3E: {
                        RolOperation(AddressingMode.AbsoluteX);
                        IncrementCycleCount();
                        break;
                    }
                //ROR Rotate Right 1 Bit Memory or Accumulator, Accumulator, 1 Bytes, 2 Cycles
                case 0x6A: {
                        RorOperation(AddressingMode.Accumulator);
                        break;
                    }
                //ROR Rotate Right 1 Bit Memory or Accumulator, Zero Page, 2 Bytes, 5 Cycles
                case 0x66: {
                        RorOperation(AddressingMode.ZeroPage);
                        break;
                    }
                //ROR Rotate Right 1 Bit Memory or Accumulator, Zero PageX, 2 Bytes, 6 Cycles
                case 0x76: {
                        RorOperation(AddressingMode.ZeroPageX);
                        break;
                    }
                //ROR Rotate Right 1 Bit Memory or Accumulator, Absolute, 3 Bytes, 6 Cycles
                case 0x6E: {
                        RorOperation(AddressingMode.Absolute);
                        break;
                    }
                //ROR Rotate Right 1 Bit Memory or Accumulator, AbsoluteX, 3 Bytes, 7 Cycles
                case 0x7E: {
                        RorOperation(AddressingMode.AbsoluteX);
                        IncrementCycleCount();
                        break;
                    }

                // === Store in Memory ==========================================================================================================
                // ==============================================================================================================================

                //STA Store Accumulator In Memory, Zero Page, 2 Bytes, 3 Cycles
                case 0x85: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.ZeroPage), (byte)A);
                        break;
                    }
                //STA Store Accumulator In Memory, Zero Page X, 2 Bytes, 4 Cycles
                case 0x95: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.ZeroPageX), (byte)A);
                        break;
                    }
                //STA Store Accumulator In Memory, Absolute, 3 Bytes, 4 Cycles
                case 0x8D: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.Absolute), (byte)A);
                        break;
                    }
                //STA Store Accumulator In Memory, Absolute X, 3 Bytes, 5 Cycles
                case 0x9D: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.AbsoluteX), (byte)A);
                        IncrementCycleCount();
                        break;
                    }
                //STA Store Accumulator In Memory, Absolute Y, 3 Bytes, 5 Cycles
                case 0x99: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.AbsoluteY), (byte)A);
                        IncrementCycleCount();
                        break;
                    }
                //STA Store Accumulator In Memory, Indexed Indirect, 2 Bytes, 6 Cycles
                case 0x81: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.IndirectX), (byte)A);
                        break;
                    }
                //STA Store Accumulator In Memory, Indirect Indexed, 2 Bytes, 6 Cycles
                case 0x91: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.IndirectY), (byte)A);
                        IncrementCycleCount();
                        break;
                    }
                //STX Store Index X, Zero Page, 2 Bytes, 3 Cycles
                case 0x86: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.ZeroPage), (byte)X);
                        break;
                    }
                //STX Store Index X, Zero Page Y, 2 Bytes, 4 Cycles
                case 0x96: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.ZeroPageY), (byte)X);
                        break;
                    }
                //STX Store Index X, Absolute, 3 Bytes, 4 Cycles
                case 0x8E: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.Absolute), (byte)X);
                        break;
                    }
                //STY Store Index Y, Zero Page, 2 Bytes, 3 Cycles
                case 0x84: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.ZeroPage), (byte)Y);
                        break;
                    }
                //STY Store Index Y, Zero Page X, 2 Bytes, 4 Cycles
                case 0x94: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.ZeroPageX), (byte)Y);
                        break;
                    }
                //STY Store Index Y, Absolute, 2 Bytes, 4 Cycles
                case 0x8C: {
                        WriteMemoryValue(GetAddressByAddressingMode(AddressingMode.Absolute), (byte)Y);
                        break;
                    }

                // === Transfer between register ================================================================================================
                // ==============================================================================================================================

                //TAX Transfer Accumulator to X Register, Implied, 1 Bytes, 2 Cycles
                case 0xAA:
                    IncrementCycleCount();
                    X = A;
                    SetNegativeFlag(X);
                    SetZeroFlag(X);
                    break;
                //TAY Transfer Accumulator to Y Register, 1 Bytes, 2 Cycles
                case 0xA8:
                    IncrementCycleCount();
                    Y = A;
                    SetNegativeFlag(Y);
                    SetZeroFlag(Y);
                    break;
                //TXA Transfer X Register to Accumulator, Implied, 1 Bytes, 2 Cycles
                case 0x8A:
                    IncrementCycleCount();
                    A = X;
                    SetNegativeFlag(A);
                    SetZeroFlag(A);
                    break;

                //TYA Transfer Y Register to Accumulator, Implied, 1 Bytes, 2 Cycles
                case 0x98:
                    IncrementCycleCount();
                    A = Y;
                    SetNegativeFlag(A);
                    SetZeroFlag(A);
                    break;

                // === NOP ======================================================================================================================
                // ==============================================================================================================================

                //NOP Operation, Implied, 1 Byte, 2 Cycles
                case 0xEA:
                    IncrementCycleCount();
                    break;
                default:
                    throw new NotSupportedException(string.Format("The OpCode ${0:X2} is not supported", IR));
            }
        }

        /// <summary>
        /// Sets the IsSignNegative register
        /// </summary>
        protected void SetNegativeFlag(int value) {
            //on the 6502, any value greater than 127 is negative. 128 = 1000000 in Binary. the 8th bit is set, therefore the number is a negative number.
            FlagN = value > 127;
        }

        /// <summary>
        /// Sets the IsResultZero register
        /// </summary>
        protected void SetZeroFlag(int value) {
            FlagZ = value == 0;
        }

        /// <summary>
        /// Uses the AddressingMode to return the correct address based on the mode.
        /// Note: This method will not increment the program counter for any mode.
        /// Note: This method will return an error if called for either the immediate or accumulator modes. 
        /// </summary>
        /// <param name="addressingMode">The addressing Mode to use</param>
        /// <returns>The memory Location</returns>
        protected int GetAddressByAddressingMode(AddressingMode addressingMode) {
            int address;
            int highByte;
            switch (addressingMode) {
                case (AddressingMode.Absolute): {
                        return (ReadMemoryValue(PC++) | (ReadMemoryValue(PC++) << 8));
                    }
                case AddressingMode.AbsoluteX: {
                        //Get the low half of the address
                        address = ReadMemoryValue(PC++);

                        //Get the high byte
                        highByte = ReadMemoryValue(PC++);

                        //We crossed a page boundry, so an extra read has occurred.
                        //However, if this is an ASL, LSR, DEC, INC, ROR, ROL or STA operation, we do not decrease it by 1.
                        if (address + X > 0xFF) {
                            switch (IR) {
                                case 0x1E:
                                case 0xDE:
                                case 0xFE:
                                case 0x5E:
                                case 0x3E:
                                case 0x7E:
                                case 0x9D: {
                                        //This is a Read Fetch Write Operation, so we don't make the extra read.
                                        return ((highByte << 8 | address) + X) & 0xFFFF;
                                    }
                                default: {
                                        ReadMemoryValue((((highByte << 8 | address) + X) - 0xFF) & 0xFFFF);
                                        break;
                                    }
                            }
                        }

                        return ((highByte << 8 | address) + X) & 0xFFFF;
                    }
                case AddressingMode.AbsoluteY: {
                        //Get the low half of the address
                        address = ReadMemoryValue(PC++);

                        //Get the high byte
                        highByte = ReadMemoryValue(PC++);

                        //We crossed a page boundry, so decrease the number of cycles by 1 if the operation is not STA
                        if (address + Y > 0xFF && IR != 0x99) {
                            ReadMemoryValue((((highByte << 8 | address) + Y) - 0xFF) & 0xFFFF);
                        }

                        //Bitshift the high byte into place, AND with FFFF to handle wrapping.
                        return ((highByte << 8 | address) + Y) & 0xFFFF;
                    }
                case AddressingMode.Immediate: {
                        return PC++;
                    }
                case AddressingMode.IndirectX: {
                        //Get the location of the address to retrieve
                        address = ReadMemoryValue(PC++);
                        ReadMemoryValue(address);

                        address += X;

                        //Now get the final Address. The is not a zero page address either.
                        var finalAddress = ReadMemoryValue((address & 0xFF)) | (ReadMemoryValue((address + 1) & 0xFF) << 8);
                        return finalAddress;
                    }
                case AddressingMode.IndirectY: {
                        address = ReadMemoryValue(PC++);

                        var finalAddress = ReadMemoryValue(address) + (ReadMemoryValue((address + 1) & 0xFF) << 8);

                        if ((finalAddress & 0xFF) + Y > 0xFF && IR != 0x91) {
                            ReadMemoryValue((finalAddress + Y - 0xFF) & 0xFFFF);
                        }

                        return (finalAddress + Y) & 0xFFFF;
                    }
                case AddressingMode.Relative: {
                        return PC;
                    }
                case (AddressingMode.ZeroPage): {
                        address = ReadMemoryValue(PC++);
                        return address;
                    }
                case (AddressingMode.ZeroPageX): {
                        address = ReadMemoryValue(PC++);
                        ReadMemoryValue(address);

                        address += X;
                        address &= 0xFF;

                        //This address wraps if its greater than 0xFF
                        if (address > 0xFF) {
                            address -= 0x100;
                            return address;
                        }

                        return address;
                    }
                case (AddressingMode.ZeroPageY): {
                        address = ReadMemoryValue(PC++);
                        ReadMemoryValue(address);

                        address += Y;
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

            var newProgramCounter = PC + movement;

            //This makes sure that we always land on the correct spot for a positive number
            if (movement >= 0)
                newProgramCounter++;

            //We Crossed a Page Boundary. So we increment the cycle counter by one. The +1 is because we always check from the end of the instruction not the beginning
            if (((PC + 1 ^ newProgramCounter) & 0xff00) != 0x0000) {
                IncrementCycleCount();
            }

            PC = newProgramCounter;
            ReadMemoryValue(PC);
        }

        /// <summary>
        /// Returns a the value from the stack without changing the position of the stack pointer
        /// </summary>

        /// <returns>The value at the current Stack Pointer</returns>
        private byte PeekStack() {
            //The stack lives at 0x100-0x1FF, but the value is only a byte so it needs to be translated
            return ReadMemoryValueWithoutCycle(SP + 0x100);
        }

        /// <summary>
        /// Write a value directly to the stack without modifying the Stack Pointer
        /// </summary>
        /// <param name="value">The value to be written to the stack</param>
        private void PokeStack(byte value) {
            //The stack lives at 0x100-0x1FF, but the value is only a byte so it needs to be translated
            WriteMemoryValueWithoutCycle(SP + 0x100, value);
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
            AddressingMode addressMode = Utility.GetAddressingMode(IR);
            int currentProgramCounter = PC;

            currentProgramCounter = WrapProgramCounter(currentProgramCounter);
            int? address1 = ReadMemoryValueWithoutCycle(currentProgramCounter++);

            currentProgramCounter = WrapProgramCounter(currentProgramCounter);
            int? address2 = ReadMemoryValueWithoutCycle(currentProgramCounter++);

            string disassembledStep = string.Empty;

            switch (addressMode) {
                case AddressingMode.Absolute: {
                        disassembledStep = string.Format("${0}{1}", address2.Value.ToString("X").PadLeft(2, '0'), address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case AddressingMode.AbsoluteX: {
                        disassembledStep = string.Format("${0}{1},X", address2.Value.ToString("X").PadLeft(2, '0'), address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case AddressingMode.AbsoluteY: {
                        disassembledStep = string.Format("${0}{1},Y", address2.Value.ToString("X").PadLeft(2, '0'), address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case AddressingMode.Accumulator: {
                        address1 = null;
                        address2 = null;

                        disassembledStep = "A";
                        break;
                    }
                case AddressingMode.Immediate: {
                        disassembledStep = string.Format("#${0}", address1.Value.ToString("X").PadLeft(4, '0'));
                        address2 = null;
                        break;
                    }
                case AddressingMode.Implied: {
                        address1 = null;
                        address2 = null;
                        break;
                    }
                case AddressingMode.Indirect: {
                        disassembledStep = string.Format("(${0}{1})", address2.Value.ToString("X").PadLeft(2, '0'), address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case AddressingMode.IndirectX: {
                        address2 = null;

                        disassembledStep = string.Format("(${0},X)", address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case AddressingMode.IndirectY: {
                        address2 = null;

                        disassembledStep = string.Format("(${0}),Y", address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case AddressingMode.Relative: {
                        var valueToMove = (byte)address1.Value;

                        var movement = valueToMove > 127 ? (valueToMove - 255) : valueToMove;

                        var newProgramCounter = PC + movement;

                        //This makes sure that we always land on the correct spot for a positive number
                        if (movement >= 0)
                            newProgramCounter++;

                        var stringAddress = PC.ToString("X").PadLeft(4, '0');

                        address1 = int.Parse(stringAddress.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                        address2 = int.Parse(stringAddress.Substring(2, 2), NumberStyles.AllowHexSpecifier);

                        disassembledStep = string.Format("${0}", newProgramCounter.ToString("X").PadLeft(4, '0'));

                        break;
                    }
                case AddressingMode.ZeroPage: {
                        address2 = null;

                        disassembledStep = string.Format("${0}", address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case AddressingMode.ZeroPageX: {
                        address2 = null;

                        disassembledStep = string.Format("${0},X", address1.Value.ToString("X").PadLeft(2, '0'));
                        break;
                    }
                case AddressingMode.ZeroPageY: {
                        address2 = null;

                        disassembledStep = string.Format("${0},Y", address1.Value.ToString("X").PadLeft(4, '0'));
                        break;
                    }
                default:
                    throw new InvalidEnumArgumentException("Invalid Addressing Mode");

            }


            CurrentDisassembly = new Disassembly {
                HighAddress = address2.HasValue ? address2.Value.ToString("X").PadLeft(2, '0') : string.Empty,
                LowAddress = address1.HasValue ? address1.Value.ToString("X").PadLeft(2, '0') : string.Empty,
                OpCodeString = IR.ConvertOpCodeIntoString(),
                DisassemblyOutput = disassembledStep
            };

            _Logger.Debug("{0} : {1}{2}{3} {4} {5} A: {6} X: {7} Y: {8} SP {9} N: {10} V: {11} B: {12} D: {13} I: {14} Z: {15} C: {16}",
                             PC.ToString("X").PadLeft(4, '0'),
                             IR.ToString("X").PadLeft(2, '0'),
                             CurrentDisassembly.LowAddress,
                             CurrentDisassembly.HighAddress,

                             CurrentDisassembly.OpCodeString,
                             CurrentDisassembly.DisassemblyOutput.PadRight(10, ' '),

                             A.ToString("X").PadLeft(3, '0'),
                             X.ToString("X").PadLeft(3, '0'),
                             Y.ToString("X").PadLeft(3, '0'),
                             SP.ToString("X").PadLeft(3, '0'),
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
        protected void AddWithCarryOperation(AddressingMode addressingMode) {
            //Accumulator, Carry = Accumulator + ValueInMemoryLocation + Carry 
            byte memoryValue = ReadMemoryValue(GetAddressByAddressingMode(addressingMode));
            int newValue = memoryValue + A + (FlagC ? 1 : 0);
            FlagV = (((A ^ newValue) & 0x80) != 0) && (((A ^ memoryValue) & 0x80) == 0);
            if (FlagD) {
                newValue = int.Parse(memoryValue.ToString("x")) + int.Parse(A.ToString("x")) + (FlagC ? 1 : 0);
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
            A = newValue;
        }

        /// <summary>
        /// The AND - Compare Memory with Accumulator operation
        /// </summary>
        /// <param name="addressingMode">The addressing mode being used</param>
        private void AndOperation(AddressingMode addressingMode) {
            A = ReadMemoryValue(GetAddressByAddressingMode(addressingMode)) & A;

            SetZeroFlag(A);
            SetNegativeFlag(A);
        }

        /// <summary>
        /// The ASL - Shift Left One Bit (Memory or Accumulator)
        /// </summary>
        /// <param name="addressingMode">The addressing Mode being used</param>
        public void AslOperation(AddressingMode addressingMode) {
            int value;
            var memoryAddress = 0;
            if (addressingMode == AddressingMode.Accumulator) {
                ReadMemoryValue(PC + 1);
                value = A;
            }
            else {
                memoryAddress = GetAddressByAddressingMode(addressingMode);
                value = ReadMemoryValue(memoryAddress);
            }

            //Dummy Write
            if (addressingMode != AddressingMode.Accumulator) {
                WriteMemoryValue(memoryAddress, (byte)value);
            }

            //If the 7th bit is set, then we have a carry
            FlagC = ((value & 0x80) != 0);

            //The And here ensures that if the value is greater than 255 it wraps properly.
            value = (value << 1) & 0xFE;

            SetNegativeFlag(value);
            SetZeroFlag(value);


            if (addressingMode == AddressingMode.Accumulator)
                A = value;
            else {
                WriteMemoryValue(memoryAddress, (byte)value);
            }
        }

        /// <summary>
        /// Performs the different branch operations.
        /// </summary>
        /// <param name="performBranch">Is a branch required</param>
        private void BranchOperation(bool performBranch) {
            var value = ReadMemoryValue(GetAddressByAddressingMode(AddressingMode.Relative));

            if (!performBranch) {
                PC++;
                return;
            }

            MoveProgramCounterByRelativeValue(value);
        }

        /// <summary>
        /// The bit operation, does an & comparison between a value in memory and the accumulator
        /// </summary>
        /// <param name="addressingMode"></param>
        private void BitOperation(AddressingMode addressingMode) {

            var memoryValue = ReadMemoryValue(GetAddressByAddressingMode(addressingMode));
            var valueToCompare = memoryValue & A;

            FlagV = (memoryValue & 0x40) != 0;

            SetNegativeFlag(memoryValue);
            SetZeroFlag(valueToCompare);
        }

        /// <summary>
        /// The compare operation. This operation compares a value in memory with a value passed into it.
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        /// <param name="comparisonValue">The value to compare against memory</param>
        private void CompareOperation(AddressingMode addressingMode, int comparisonValue) {
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
        private void ChangeMemoryByOne(AddressingMode addressingMode, bool decrement) {
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
            var value = useXRegister ? X : Y;
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
                X = value;
            else
                Y = value;
        }

        /// <summary>
        /// The EOR Operation, Performs an Exclusive OR Operation against the Accumulator and a value in memory
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        private void EorOperation(AddressingMode addressingMode) {
            A = A ^ ReadMemoryValue(GetAddressByAddressingMode(addressingMode));

            SetNegativeFlag(A);
            SetZeroFlag(A);
        }

        /// <summary>
        /// The LSR Operation. Performs a Left shift operation on a value in memory
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        private void LsrOperation(AddressingMode addressingMode) {
            int value;
            var memoryAddress = 0;
            if (addressingMode == AddressingMode.Accumulator) {
                ReadMemoryValue(PC + 1);
                value = A;
            }
            else {
                memoryAddress = GetAddressByAddressingMode(addressingMode);
                value = ReadMemoryValue(memoryAddress);
            }

            //Dummy Write
            if (addressingMode != AddressingMode.Accumulator) {
                WriteMemoryValue(memoryAddress, (byte)value);
            }

            FlagN = false;

            //If the Zero bit is set, we have a carry
            FlagC = (value & 0x01) != 0;

            value = (value >> 1);

            SetZeroFlag(value);
            if (addressingMode == AddressingMode.Accumulator)
                A = value;
            else {
                WriteMemoryValue(memoryAddress, (byte)value);
            }
        }

        /// <summary>
        /// The Or Operation. Performs an Or Operation with the accumulator and a value in memory
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        private void OrOperation(AddressingMode addressingMode) {
            A = A | ReadMemoryValue(GetAddressByAddressingMode(addressingMode));
            SetNegativeFlag(A);
            SetZeroFlag(A);
        }

        /// <summary>
        /// The ROL operation. Performs a rotate left operation on a value in memory.
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        private void RolOperation(AddressingMode addressingMode) {
            int value;
            var memoryAddress = 0;
            if (addressingMode == AddressingMode.Accumulator) {
                //Dummy Read
                ReadMemoryValue(PC + 1);
                value = A;
            }
            else {
                memoryAddress = GetAddressByAddressingMode(addressingMode);
                value = ReadMemoryValue(memoryAddress);
            }

            //Dummy Write
            if (addressingMode != AddressingMode.Accumulator) {
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


            if (addressingMode == AddressingMode.Accumulator)
                A = value;
            else {
                WriteMemoryValue(memoryAddress, (byte)value);
            }
        }

        /// <summary>
        /// The ROR operation. Performs a rotate right operation on a value in memory.
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        private void RorOperation(AddressingMode addressingMode) {
            int value;
            var memoryAddress = 0;
            if (addressingMode == AddressingMode.Accumulator) {
                //Dummy Read
                ReadMemoryValue(PC + 1);
                value = A;
            }
            else {
                memoryAddress = GetAddressByAddressingMode(addressingMode);
                value = ReadMemoryValue(memoryAddress);
            }

            //Dummy Write
            if (addressingMode != AddressingMode.Accumulator) {
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

            if (addressingMode == AddressingMode.Accumulator)
                A = value;
            else {
                WriteMemoryValue(memoryAddress, (byte)value);
            }
        }

        /// <summary>
        /// The SBC operation. Performs a subtract with carry operation on the accumulator and a value in memory.
        /// </summary>
        /// <param name="addressingMode">The addressing mode to use</param>
        protected virtual void SubtractWithBorrowOperation(AddressingMode addressingMode) {
            byte memoryValue = ReadMemoryValue(GetAddressByAddressingMode(addressingMode));
            int newValue = FlagD
                               ? int.Parse(A.ToString("x")) - int.Parse(memoryValue.ToString("x")) - (FlagC ? 0 : 1)
                               : A - memoryValue - (FlagC ? 0 : 1);

            FlagC = newValue >= 0;

            if (FlagD) {
                if (newValue < 0) {
                    newValue += 100;
                }
                newValue = (int)Convert.ToInt64(string.Concat("0x", newValue), 16);
            }
            else {
                FlagV = (((A ^ newValue) & 0x80) != 0) && (((A ^ memoryValue) & 0x80) != 0);
                if (newValue < 0) {
                    newValue += 256;
                }
            }

            SetNegativeFlag(newValue);
            SetZeroFlag(newValue);

            A = newValue;
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
            PokeStack((byte)(((PC + 1) >> 8) & 0xFF));
            SP--;
            IncrementCycleCount();

            PokeStack((byte)((PC + 1) & 0xFF));
            SP--;
            IncrementCycleCount();

            PC = GetAddressByAddressingMode(AddressingMode.Absolute);
        }

        /// <summary>
        /// The RTS routine. Called when returning from a subroutine.
        /// </summary>
        private void ReturnFromSubRoutineOperation() {
            ReadMemoryValue(++PC);
            SP++;
            IncrementCycleCount();

            var lowBit = PeekStack();
            SP++;
            IncrementCycleCount();

            var highBit = PeekStack() << 8;
            IncrementCycleCount();

            PC = (highBit | lowBit) + 1;
            IncrementCycleCount();
        }


        /// <summary>
        /// The BRK routine. Called when a BRK occurs.
        /// </summary>
        private void BreakOperation(bool isBrk, int vector) {
            ReadMemoryValue(++PC);
            //Put the high value on the stack
            //When we RTI the address will be incremented by one, and the address after a break will not be used.
            PokeStack((byte)(((PC) >> 8) & 0xFF));
            SP--;
            IncrementCycleCount();

            //Put the low value on the stack
            PokeStack((byte)((PC) & 0xFF));
            SP--;
            IncrementCycleCount();

            //We only set the Break Flag is a Break Occurs
            if (isBrk)
                PokeStack((byte)(ConvertFlagsToByte(true) | 0x10));
            else
                PokeStack(ConvertFlagsToByte(false));

            SP--;
            IncrementCycleCount();

            FlagI = true;

            PC = (ReadMemoryValue(vector + 1) << 8) | ReadMemoryValue(vector);

            _PreviousInterrupt = false;
        }

        /// <summary>
        /// The RTI routine. Called when returning from a BRK opertion.
        /// Note: when called after a BRK operation the Program Counter is not set to the location after the BRK,
        /// it is set +1
        /// </summary>
        private void ReturnFromInterruptOperation() {
            ReadMemoryValue(++PC);
            SP++;
            IncrementCycleCount();

            PullFlagsOperation();
            SP++;
            IncrementCycleCount();

            var lowBit = PeekStack();
            SP++;
            IncrementCycleCount();

            var highBit = PeekStack() << 8;
            IncrementCycleCount();

            PC = (highBit | lowBit);
        }

        /// <summary>
        /// This is ran anytime an NMI occurrs
        /// </summary>
	    private void ProcessNMI() {
            PC--;
            BreakOperation(false, VectorNMI);
            IR = ReadMemoryValue(PC);
            SetDisassembly();
        }

        /// <summary>
        /// This is ran anytime an IRQ occurrs
        /// </summary>
        private void ProcessIRQ() {
            if (FlagI) {
                return;
            }
            PC--;
            BreakOperation(false, VectorIRQ);
            IR = ReadMemoryValue(PC);
            SetDisassembly();
        }
    }
}