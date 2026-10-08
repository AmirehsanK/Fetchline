using Fetchline.Core.Isa;
using Fetchline.Core.Pipeline;

namespace Fetchline.Viz.State;

/// <summary>One register, as the registers pane shows it.</summary>
/// <param name="Written">It was written in the cycle being shown, by the instruction in WB.</param>
public readonly record struct RegisterCell(int Index, string Name, uint Value, bool Written);

/// <summary>The thirty-two registers after the cycle a session is showing.</summary>
public static class RegisterBank
{
    /// <param name="style">How they are named: as the program names them, usually.</param>
    public static IReadOnlyList<RegisterCell> Of(Session session, RegisterStyle style = RegisterStyle.Abi)
    {
        // A cycle has one instruction in WB, so it writes one register at most.
        var written = session.Record?.Events.OfType<RegWriteEvent>().Select(write => (int)write.Register).FirstOrDefault(-1) ?? -1;
        var registers = session.Registers;
        var cells = new RegisterCell[Registers.Count];
        for (var index = 0; index < cells.Length; index++)
        {
            cells[index] = new RegisterCell(index, Registers.Name(index, style), registers[index], index == written);
        }

        return cells;
    }
}
