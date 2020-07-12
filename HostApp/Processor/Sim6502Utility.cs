using System;
using System.ComponentModel;
using System.Diagnostics;

namespace HostApp.Processor {
    public static class Sim6502Utility {

        [Conditional("DEBUG")]
        internal static void SetDisassembly(Sim6502 processor, HostLogger logger) {
            if (logger == null || !logger.IsDebugEnabled) {
                return;
            }
            EAddressingMode addressMode = Sim6502Utility.GetAddressingMode(processor.RegisterIR);
            int currentProgramCounter = processor.RegisterPC;

            currentProgramCounter = processor.WrapProgramCounter(++currentProgramCounter);
            int? address1 = processor.ReadMemoryValueWithoutCycle(currentProgramCounter);

            currentProgramCounter = processor.WrapProgramCounter(++currentProgramCounter);
            int? address2 = processor.ReadMemoryValueWithoutCycle(currentProgramCounter);

            string disassembledStep = string.Empty;

            switch (addressMode) {
                case EAddressingMode.Absolute:
                    disassembledStep = string.Format("${0}{1}", address2.Value.ToString("X").PadLeft(2, '0'), address1.Value.ToString("X").PadLeft(2, '0'));
                    break;
                case EAddressingMode.AbsoluteX:
                    disassembledStep = string.Format("${0}{1},X", address2.Value.ToString("X").PadLeft(2, '0'), address1.Value.ToString("X").PadLeft(2, '0'));
                    break;
                case EAddressingMode.AbsoluteY:
                    disassembledStep = string.Format("${0}{1},Y", address2.Value.ToString("X").PadLeft(2, '0'), address1.Value.ToString("X").PadLeft(2, '0'));
                    break;
                case EAddressingMode.Accumulator:
                    address1 = null;
                    address2 = null;
                    disassembledStep = "A";
                    break;
                case EAddressingMode.Immediate:
                    disassembledStep = string.Format("#${0}", address1.Value.ToString("X").PadLeft(4, '0'));
                    address2 = null;
                    break;
                case EAddressingMode.Implicit:
                    address1 = null;
                    address2 = null;
                    break;
                case EAddressingMode.Indirect:
                    disassembledStep = string.Format("(${0}{1})", address2.Value.ToString("X").PadLeft(2, '0'), address1.Value.ToString("X").PadLeft(2, '0'));
                    break;
                case EAddressingMode.IndirectX:
                    address2 = null;
                    disassembledStep = string.Format("(${0},X)", address1.Value.ToString("X").PadLeft(2, '0'));
                    break;
                case EAddressingMode.IndirectY:
                    address2 = null;
                    disassembledStep = string.Format("(${0}),Y", address1.Value.ToString("X").PadLeft(2, '0'));
                    break;
                case EAddressingMode.Relative:
                    var valueToMove = (byte)address1.Value;
                    var movement = valueToMove > 127 ? (valueToMove - 255) : valueToMove;
                    var newProgramCounter = processor.RegisterPC + movement;
                    //This makes sure that we always land on the correct spot for a positive number
                    if (movement >= 0) {
                        newProgramCounter++;
                    }
                    var stringAddress = processor.RegisterPC.ToString("X").PadLeft(4, '0');
                    // address1 = int.Parse(stringAddress.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                    address2 = null; // int.Parse(stringAddress.Substring(2, 2), NumberStyles.AllowHexSpecifier);
                    disassembledStep = string.Format("${0}", newProgramCounter.ToString("X").PadLeft(4, '0'));
                    break;
                case EAddressingMode.ZeroPage:
                    address2 = null;
                    disassembledStep = string.Format("${0}", address1.Value.ToString("X").PadLeft(2, '0'));
                    break;
                case EAddressingMode.ZeroPageX:
                    address2 = null;
                    disassembledStep = string.Format("${0},X", address1.Value.ToString("X").PadLeft(2, '0'));
                    break;
                case EAddressingMode.ZeroPageY:
                    address2 = null;
                    disassembledStep = string.Format("${0},Y", address1.Value.ToString("X").PadLeft(4, '0'));
                    break;
                default:
                    throw new InvalidEnumArgumentException("Invalid Addressing Mode");
            }

            string opcodeAsString = processor.RegisterIR.ConvertOpCodeIntoString();

            logger.Debug("{0} : {1} {2} {3} {4} {5} A: {6} X: {7} Y: {8} SP {9} N: {10} V: {11} B: {12} D: {13} I: {14} Z: {15} C: {16} $0D={17}",
                             processor.RegisterPC.ToString("X4"),
                             processor.RegisterIR.ToString("X2"),
                             address1.HasValue ? address1.Value.ToString("X").PadLeft(2, '0') : "  ",
                             address2.HasValue ? address2.Value.ToString("X").PadLeft(2, '0') : "  ",

                             opcodeAsString,
                             disassembledStep.PadRight(13 - opcodeAsString.Length, ' '),

                             processor.RegisterA.ToString("X").PadLeft(3, '0'),
                             processor.RegisterX.ToString("X").PadLeft(3, '0'),
                             processor.RegisterY.ToString("X").PadLeft(3, '0'),
                             processor.RegisterSP.ToString("X").PadLeft(3, '0'),
                             Convert.ToInt16(processor.FlagN),
                             Convert.ToInt16(processor.FlagV),
                             0,
                             Convert.ToInt16(processor.FlagD),
                             Convert.ToInt16(processor.FlagI),
                             Convert.ToInt16(processor.FlagZ),
                             Convert.ToInt16(processor.FlagC),
                             processor.ReadMemoryValueWithoutCycle(0x0D).ToString("X"));
        }

        public static string ConvertOpCodeIntoString(this int opcode) {
            switch (opcode) {
                case 0x69:  // ADC Immediate
                case 0x65:  // ADC Zero Page
                case 0x75:  // ADC Zero Page X
                case 0x6D:  // ADC Absolute
                case 0x7D:  // ADC Absolute X
                case 0x79:  // ADC Absolute Y
                case 0x61:  // ADC Indrect X
                case 0x71:  // ADC Indirect Y
                case 0x72:  // ADC Indirect ZP
                    return "ADC";
                case 0x29:  // AND Immediate
                case 0x25:  // AND Zero Page
                case 0x35:  // AND Zero Page X
                case 0x2D:  // AND Absolute
                case 0x3D:  // AND Absolute X
                case 0x39:  // AND Absolute Y
                case 0x21:  // AND Indirect X
                case 0x31:  // AND Indirect Y
                case 0x32:  // AND Indirect ZP
                    return "AND";
                case 0x0A:  // ASL Accumulator
                case 0x06:  // ASL Zero Page
                case 0x16:  // ASL Zero Page X
                case 0x0E:  // ASL Absolute
                case 0x1E:  // ASL Absolute X
                    return "ASL";
                case 0x0F:
                    return "BBR0";
                case 0x1F:
                    return "BBR1";
                case 0x2F:
                    return "BBR2";
                case 0x3F:
                    return "BBR3";
                case 0x4F:
                    return "BBR4";
                case 0x5F:
                    return "BBR5";
                case 0x6F:
                    return "BBR6";
                case 0x7F:
                    return "BBR7";
                case 0x8F:
                    return "BBS0";
                case 0x9F:
                    return "BBS1";
                case 0xAF:
                    return "BBS2";
                case 0xBF:
                    return "BBS3";
                case 0xCF:
                    return "BBS4";
                case 0xDF:
                    return "BBS5";
                case 0xEF:
                    return "BBS6";
                case 0xFF:
                    return "BBS7";
                case 0x90:  // BCC Relative
                    return "BCC";
                case 0xB0:  // BCS Relative
                    return "BCS";
                case 0xF0:  // BEQ Relative
                    return "BEQ";
                case 0x24:  // BIT Zero Page
                case 0x2C:  // BIT Absolute
                case 0x34:  // BIT Zero Page X
                case 0x3C:  // BIT Absolute X
                case 0x89:  // BIT Immediate
                    return "BIT";
                case 0x30:  // BMI Relative
                    return "BMI";
                case 0xD0:  // BNE Relative
                    return "BNE";
                case 0x10:  // BPL Relative
                    return "BPL";
                case 0x00:  // BRK Implied
                    return "BRK";
                case 0x50: // BVC Relative
                    return "BCV";
                case 0x70: // BVS Relative
                    return "BVS";
                case 0x80: // BRA Relative
                    return "BRA";
                case 0x18:  // CLC Implied
                    return "CLC";
                case 0xD8:  // CLD Implied
                    return "CLD";
                case 0x58:  // CLI Implied
                    return "CLI";
                case 0xB8:  // CLV Implied
                    return "CLV";
                case 0xC9:  // CMP Immediate
                case 0xC5:  // CMP ZeroPage
                case 0xD5:  // CMP Zero Page X
                case 0xCD:  // CMP Absolute
                case 0xDD:  // CMP Absolute X
                case 0xD9:  // CMP Absolute Y
                case 0xC1:  // CMP Indirect X
                case 0xD1:  // CMP Indirect Y
                case 0xD2:  // CMP Indirect ZP
                    return "CMP";
                case 0xE0:  // CPX Immediate
                case 0xE4:  // CPX ZeroPage
                case 0xEC:  // CPX Absolute
                    return "CPX";
                case 0xC0:  // CPY Immediate
                case 0xC4:  // CPY ZeroPage
                case 0xCC:  // CPY Absolute
                    return "CPY";
                case 0xC6:  // DEC Zero Page
                case 0xD6:  // DEC Zero Page X
                case 0xCE:  // DEC Absolute
                case 0xDE:  // DEC Absolute X
                    return "DEC";
                case 0xCA:  // DEX Implied
                    return "DEX";
                case 0x88:  // DEY Implied
                    return "DEY";
                case 0x49:  // EOR Immediate
                case 0x45:  // EOR Zero Page
                case 0x55:  // EOR Zero Page X
                case 0x4D:  // EOR Absolute
                case 0x5D:  // EOR Absolute X
                case 0x59:  // EOR Absolute Y
                case 0x41:  // EOR Indrect X
                case 0x51:  // EOR Indirect Y
                case 0x52:  // EOR Indirect ZP
                    return "EOR";
                case 0xE6:  // INC Zero Page
                case 0xF6:  // INC Zero Page X
                    return "INC";
                case 0xE8:  // INX Implied
                    return "INX";
                case 0xC8:  // INY Implied
                    return "INY";
                case 0xEE:  // INC Absolute
                case 0xFE:  // INC Absolute X
                    return "INC";
                case 0x1A: // INC A
                    return "INC A";
                case 0x3A: // DEC A
                    return "DEC A";
                case 0x4C:  // JMP Absolute
                case 0x6C:  // JMP Indirect
                case 0x7C:  // JMP Absolute X
                    return "JMP";
                case 0x20:  // JSR Absolute
                    return "JSR";
                case 0xA9:  // LDA Immediate
                case 0xA5:  // LDA Zero Page
                case 0xB5:  // LDA Zero Page X
                case 0xAD:  // LDA Absolute
                case 0xBD:  // LDA Absolute X
                case 0xB9:  // LDA Absolute Y
                case 0xA1:  // LDA Indirect X
                case 0xB1:  // LDA Indirect Y
                case 0xB2:  // LDA Indirect ZP
                    return "LDA";
                case 0xA2:  // LDX Immediate
                case 0xA6:  // LDX Zero Page
                case 0xB6:  // LDX Zero Page Y
                case 0xAE:  // LDX Absolute
                case 0xBE:  // LDX Absolute Y
                    return "LDX";
                case 0xA0:  // LDY Immediate
                case 0xA4:  // LDY Zero Page
                case 0xB4:  // LDY Zero Page Y
                case 0xAC:  // LDY Absolute
                case 0xBC:  // LDY Absolute Y
                    return "LDY";
                case 0x4A:  // LSR Accumulator
                case 0x46:  // LSR Zero Page
                case 0x56:  // LSR Zero Page X
                case 0x4E:  // LSR Absolute
                case 0x5E:  // LSR Absolute X
                    return "LSR";
                case 0xEA:  // NOP Implied
                case 0x03:  // NOP Implied
                case 0x13:  // NOP Implied
                case 0x23:  // NOP Implied
                case 0x33:  // NOP Implied
                case 0x43:  // NOP Implied
                case 0x53:  // NOP Implied
                case 0x63:  // NOP Implied
                case 0x73:  // NOP Implied
                case 0x83:  // NOP Implied
                case 0x93:  // NOP Implied
                case 0xA3:  // NOP Implied
                case 0xB3:  // NOP Implied
                case 0xC3:  // NOP Implied
                case 0xD3:  // NOP Implied
                case 0xE3:  // NOP Implied
                case 0xF3:  // NOP Implied
                case 0x0B:  // NOP Implied
                case 0x1B:  // NOP Implied
                case 0x2B:  // NOP Implied
                case 0x3B:  // NOP Implied
                case 0x4B:  // NOP Implied
                case 0x5B:  // NOP Implied
                case 0x6B:  // NOP Implied
                case 0x7B:  // NOP Implied
                case 0x8B:  // NOP Implied
                case 0x9B:  // NOP Implied
                case 0xAB:  // NOP Implied
                case 0xBB:  // NOP Implied
                case 0xEB:  // NOP Implied
                case 0xFB:  // NOP Implied
                case 0x02:  // NOP Immediate
                case 0x22:  // NOP Immediate
                case 0x42:  // NOP Immediate
                case 0x62:  // NOP Immediate
                case 0x82:  // NOP Immediate
                case 0xC2:  // NOP Immediate
                case 0xE2:  // NOP Immediate
                case 0x44:  // NOP Immediate
                case 0x54:  // NOP Immediate
                case 0xD4:  // NOP Immediate
                case 0xF4:  // NOP Immediate
                case 0x5C: // NOP Absolute
                case 0xDC: // NOP Absolute
                case 0xFC: // NOP Absolute
                    return "NOP";
                case 0x09:  // ORA Immediate
                case 0x05:  // ORA Zero Page
                case 0x15:  // ORA Zero Page X
                case 0x0D:  // ORA Absolute
                case 0x1D:  // ORA Absolute X
                case 0x19:  // ORA Absolute Y
                case 0x01:  // ORA Indirect X
                case 0x11:  // ORA Indirect Y
                case 0x12:  // ORA Indirect ZP
                    return "ORA";
                case 0x48:  // PHA Implied
                    return "PHA";
                case 0x08:  // PHP Implied
                    return "PHP";
                case 0x68:  // PLA Implied
                    return "PLA";
                case 0xDA:  // PHX Implied
                    return "PHX";
                case 0x5A:  // PHY Implied
                    return "PHY";
                case 0x28:  // PLP Implied
                    return "PLP";
                case 0xFA:  // PLX Implied
                    return "PLX";
                case 0x7A:  // PLY Implied
                    return "PLY";
                case 0x07:
                    return "RMB 0";
                case 0x17:
                    return "RMB 1";
                case 0x27:
                    return "RMB 2";
                case 0x37:
                    return "RMB 3";
                case 0x47:
                    return "RMB 4";
                case 0x57:
                    return "RMB 5";
                case 0x67:
                    return "RMB 6";
                case 0x77:
                    return "RMB 7";
                case 0x87:
                    return "SMB 0";
                case 0x97:
                    return "SMB 1";
                case 0xA7:
                    return "SMB 2";
                case 0xB7:
                    return "SMB 3";
                case 0xC7:
                    return "SMB 4";
                case 0xD7:
                    return "SMB 5";
                case 0xE7:
                    return "SMB 6";
                case 0xF7:
                    return "SMB 7";
                case 0x2A:  // ROL Accumulator
                case 0x26:  // ROL Zero Page
                case 0x36:  // ROL Zero Page X
                case 0x2E:  // ROL Absolute
                case 0x3E:  // ROL Absolute X
                    return "ROL";
                case 0x6A:  // ROR Accumulator
                case 0x66:  // ROR Zero Page
                case 0x76:  // ROR Zero Page X
                case 0x6E:  // ROR Absolute
                case 0x7E:  // ROR Absolute X
                    return "ROR";
                case 0x40:  // RTI Implied
                    return "RTI";
                case 0x60:  // RTS Implied
                    return "RTS";
                case 0xE9:  // SBC Immediate
                case 0xE5:  // SBC Zero Page
                case 0xF5:  // SBC Zero Page X
                case 0xED:  // SBC Absolute
                case 0xFD:  // SBC Absolute X
                case 0xF9:  // SBC Absolute Y
                case 0xE1:  // SBC Indrect X
                case 0xF1:  // SBC Indirect Y
                case 0xF2:  // SBC Indirect ZP
                    return "SBC";
                case 0x38:  // SEC Implied
                    return "SEC";
                case 0xF8:  // SED Implied
                    return "SED";
                case 0x78:  // SEI Implied
                    return "SEI";
                case 0x85:  // STA ZeroPage
                case 0x95:  // STA Zero Page X
                case 0x8D:  // STA Absolute
                case 0x9D:  // STA Absolute X
                case 0x99:  // STA Absolute Y
                case 0x81:  // STA Indirect X
                case 0x91:  // STA Indirect Y
                case 0x92:  // STA Indirect ZP
                    return "STA";
                case 0x86:  // STX Zero Page
                case 0x96:  // STX Zero Page Y
                case 0x8E:  // STX Absolute
                    return "STX";
                case 0x84:  // STY Zero Page
                case 0x94:  // STY Zero Page X
                case 0x8C:  // STY Absolute
                    return "STY";
                case 0x64:  // STZ Zero Page
                case 0x74:  // STZ Zero Page X
                case 0x9C:  // STZ Absolute
                case 0x9E:  // STZ Absolute X
                    return "STZ";
                case 0xAA:  // TAX Implied
                    return "TAX";
                case 0xA8:  // TAY Implied
                    return "TAY";
                case 0x14: // TRB ZP
                case 0x1C: // TRB Absolute
                    return "TRB";
                case 0x04: // TSB ZP
                case 0x0C: // TSB Absolute
                    return "TSB";
                case 0xBA:  // TSX Implied
                    return "TSX";
                case 0x8A:  // TXA Implied
                    return "TXA";
                case 0x9A:  // TXS Implied
                    return "TXS";
                case 0x98:  // TYA Implied
                    return "TYA";
                default:
                    throw new InvalidEnumArgumentException($"Sim6502Utility.ConvertOpCodeIntoString: no conversion for ${opcode:X2}");

            }
        }


        public static EAddressingMode GetAddressingMode(int opcode) {
            switch (opcode) {
                case 0x0D: //ORA
                case 0x2D: //AND
                case 0x4D: //EOR
                case 0x6D: //ADC
                case 0x8D: //STA
                case 0xAD: //LDA
                case 0xCD: //CMP
                case 0xED: //SBC
                case 0x0E: //ASL
                case 0x2E: //ROL
                case 0x4E: //LSR
                case 0x6E: //ROR
                case 0x8E: //SDX
                case 0xAE: //LDX
                case 0xCE: //DEC
                case 0xEE: //INC
                case 0x2C: //Bit
                case 0x4C: //JMP
                case 0x8C: //STY
                case 0xAC: //LDY
                case 0xCC: //CPY
                case 0xEC: //CPX
                case 0x20: //JSR
                case 0x5C: // NOP Absolute
                case 0xDC: // NOP Absolute
                case 0xFC: // NOP Absolute
                case 0x1A: // INC A
                case 0x3A: // DEC A
                case 0x9C: // STZ
                case 0x0C: // TSB
                case 0x1C: // TRB
                    return EAddressingMode.Absolute;
                case 0x1D: //ORA
                case 0x3D: //AND
                case 0x5D: //EOR
                case 0x7D: //ADC
                case 0x9D: //STA
                case 0xBD: //LDA
                case 0xDD: //CMP
                case 0xFD: //SBC
                case 0xBC: //LDY
                case 0xFE: //INC
                case 0x1E: //ASL
                case 0x3E: //ROL
                case 0x5E: //LSR
                case 0x7C: //JMP
                case 0x7E: //ROR
                case 0x9E: // STZ
                case 0x3C: // BIT
                    return EAddressingMode.AbsoluteX;
                case 0x19: //ORA
                case 0x39: //AND
                case 0x59: //EOR
                case 0x79: //ADC
                case 0x99: //STA
                case 0xB9: //LDA
                case 0xD9: //CMP
                case 0xF9: //SBC
                case 0xBE: //LDX
                    return EAddressingMode.AbsoluteY;
                case 0x0A: //ASL
                case 0x4A: //LSR
                case 0x2A: //ROL
                case 0x6A: //ROR
                    return EAddressingMode.Accumulator;
                case 0x09: //ORA
                case 0x29: //AND
                case 0x49: //EOR
                case 0x69: //ADC
                case 0xA0: //LDY
                case 0xC0: //CPY
                case 0xE0: //CMP
                case 0xA2: //LDX
                case 0xA9: //LDA
                case 0xC9: //CMP
                case 0xE9: //SBC
                case 0x02: // NOP
                case 0x22: // NOP
                case 0x42: // NOP
                case 0x62: // NOP
                case 0x82: // NOP
                case 0xC2: // NOP
                case 0xE2: // NOP
                case 0x44: // NOP
                case 0x54: // NOP
                case 0xD4: // NOP
                case 0xF4: // NOP
                case 0x89: // BIT
                    return EAddressingMode.Immediate;
                case 0x00: //BRK
                case 0x18: //CLC
                case 0xD8: //CLD
                case 0x58: //CLI
                case 0xB8: //CLV
                case 0xDE: //DEC
                case 0xCA: //DEX
                case 0x88: //DEY
                case 0xE8: //INX
                case 0xC8: //INY
                case 0xEA: //NOP
                case 0x48: //PHA
                case 0x08: //PHP
                case 0x68: //PLA
                case 0x28: //PLP
                case 0x40: //RTI
                case 0x5A: //PHY
                case 0x60: //RTS
                case 0x38: //SEC
                case 0xF8: //SED
                case 0x78: //SEI
                case 0x7A: //PLY
                case 0xAA: //TAX
                case 0xA8: //TAY
                case 0xBA: //TSX
                case 0x8A: //TXA
                case 0x9A: //TXS
                case 0x98: //TYA
                case 0xDA: //PHX
                case 0xFA: //PLX
                case 0x03: //NOP
                case 0x13: //NOP
                case 0x23: //NOP
                case 0x33: //NOP
                case 0x43: //NOP
                case 0x53: //NOP
                case 0x63: //NOP
                case 0x73: //NOP
                case 0x83: //NOP
                case 0x93: //NOP
                case 0xA3: //NOP
                case 0xB3: //NOP
                case 0xC3: //NOP
                case 0xD3: //NOP
                case 0xE3: //NOP
                case 0xF3: //NOP
                case 0x0B: //NOP
                case 0x1B: //NOP
                case 0x2B: //NOP
                case 0x3B: //NOP
                case 0x4B: //NOP
                case 0x5B: //NOP
                case 0x6B: //NOP
                case 0x7B: //NOP
                case 0x8B: //NOP
                case 0x9B: //NOP
                case 0xAB: //NOP
                case 0xBB: //NOP
                case 0xEB: //NOP
                case 0xFB: //NOP
                    return EAddressingMode.Implicit;
                case 0x6C:
                    return EAddressingMode.Indirect;
                case 0x61: //ADC
                case 0x21: //AND
                case 0xC1: //CMP
                case 0x41: //EOR
                case 0xA1: //LDA
                case 0x01: //ORA
                case 0xE1: //SBC
                case 0x81: //STA
                    return EAddressingMode.IndirectX;
                case 0x71: //ADC
                case 0x31: //AND
                case 0xD1: //CMP
                case 0x51: //EOR
                case 0xB1: //LDA
                case 0x11: //ORA
                case 0xF1: //SBC
                case 0x91: //STA
                    return EAddressingMode.IndirectY;
                case 0x90: //BCC
                case 0xB0: //BCS
                case 0xF0: //BEQ
                case 0x30: //BMI
                case 0xD0: //BNE
                case 0x10: //BPL
                case 0x50: //BVC
                case 0x70: //BVS
                case 0x80: //BRA
                case 0x0F: //BBR
                case 0x1F: //BBR
                case 0x2F: //BBR
                case 0x3F: //BBR
                case 0x4F: //BBR
                case 0x5F: //BBR
                case 0x6F: //BBR
                case 0x7F: //BBR
                case 0x8F: //BBS
                case 0x9F: //BBS
                case 0xAF: //BBS
                case 0xBF: //BBS
                case 0xCF: //BBS
                case 0xDF: //BBS
                case 0xEF: //BBS
                case 0xFF: //BBS
                    return EAddressingMode.Relative;
                case 0x65: //ADC
                case 0x25: //AND
                case 0x06: //ASL
                case 0x24: //BIT
                case 0xC5: //CMP
                case 0xE4: //CPX
                case 0xC4: //CPY
                case 0xC6: //DEC
                case 0x45: //EOR
                case 0xE6: //INC
                case 0xA5: //LDA
                case 0xA6: //LDX
                case 0xA4: //LDY
                case 0x46: //LSR
                case 0x05: //ORA
                case 0x26: //ROL
                case 0x66: //ROR
                case 0xE5: //SBC
                case 0x85: //STA
                case 0x86: //STX
                case 0x84: //STY
                case 0x12:
                case 0x32:
                case 0x52:
                case 0x72:
                case 0x92:
                case 0xB2:
                case 0xD2:
                case 0xF2:
                case 0x64: // STZ
                case 0x04: // TSB
                case 0x14: // TRB
                    return EAddressingMode.ZeroPage;
                case 0x75: //ADC
                case 0x35: //AND
                case 0x16: //ASL
                case 0xD5: //CMP
                case 0xD6: //DEC
                case 0x55: //EOR
                case 0xF6: //INC
                case 0xB5: //LDA
                case 0xB6: //LDX
                case 0xB4: //LDY
                case 0x56: //LSR
                case 0x15: //ORA
                case 0x36: //ROL
                case 0x76: //ROR
                case 0xF5: //SBC
                case 0x95: //STA
                case 0x96: //STX
                case 0x94: //STY
                case 0x74: // STZ
                case 0x34: // BIT Zero Page X
                    return EAddressingMode.ZeroPageX;
                default:
                    throw new NotSupportedException($"Sim6502Utility.GetAddressingMode: ${opcode:X2} is not supported");
            }
        }
    }
}
