namespace Fetchline.Core.Isa;

/// <summary>
/// A decoded instruction. A register field the format does not have is zero. <see cref="Imm"/>
/// is the value execution uses, so what it holds depends on the format:
/// <list type="bullet">
/// <item>I, S, B, J: the immediate, sign-extended (a byte offset for branches and jumps).</item>
/// <item>U: the whole upper immediate, already shifted, with its low twelve bits zero.</item>
/// <item>Shift: the shift amount, 0 to 31.</item>
/// <item>Csr, CsrImm: the CSR number, 0 to 4095. A CsrImm keeps its constant in <see cref="Rs1"/>.</item>
/// <item>Fence: bits 31..20 as they are (<c>fm</c>, <c>pred</c>, <c>succ</c>).</item>
/// </list>
/// </summary>
/// <param name="Raw">The word this was decoded from.</param>
public readonly record struct Instruction(Op Op, byte Rd, byte Rs1, byte Rs2, int Imm, uint Raw)
{
    /// <summary>False when the word matched no row of the instruction table.</summary>
    public bool IsLegal => Op != Op.Illegal;

    /// <summary>The control signals of this instruction; all clear when it is illegal.</summary>
    public Control Control => InstructionSet.ControlOf(Op);

    /// <summary>A word that is not an instruction.</summary>
    public static Instruction Illegal(uint raw) => new(Op.Illegal, 0, 0, 0, 0, raw);
}
