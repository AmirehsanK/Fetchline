using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Fetchline.Core.Asm;
using Fetchline.Core.Pipeline;
using Fetchline.Core.Trace;

namespace Fetchline.Viz.Export;

/// <summary>
/// A run as JSON, for a program to read: the switches, then every cycle with what was in each
/// stage, the events of the cycle, the values on the wires, and the instruction that completed.
/// It is the record stream written out and nothing is added to it but each instruction as text.
///
/// Counts are JSON numbers. Anything that is a 32-bit word (an address, a value, an instruction)
/// is a string of eight hex digits, so that no reader has to wonder about its sign.
/// </summary>
public static class JsonTrace
{
    /// <summary>What a file says it is, and the version of this layout.</summary>
    public const string Format = "fetchline-trace";

    public const int Version = 1;

    public static string Write(Program program, PipelineConfig config, IReadOnlyList<CycleRecord> records)
    {
        var labels = new InstructionLabels(program);

        // The file is data and never part of a page, so a plus sign is written as a plus sign.
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("format", Format);
            json.WriteNumber("version", Version);

            json.WriteStartObject("config");
            json.WriteString("hazards", SwitchNames.Of(config.Hazards));
            json.WriteString("branch", SwitchNames.Of(config.Branches));
            json.WriteString("predictor", SwitchNames.Of(config.Predictor));
            json.WriteNumber("btb", config.BtbEntries);
            json.WriteNumber("muldiv", config.MulDivCycles);
            json.WriteEndObject();

            var stats = PipelineStats.Of(records);
            json.WriteStartObject("summary");
            json.WriteNumber("cycles", stats.Cycles);
            json.WriteNumber("instructions", stats.Instructions);
            json.WriteNumber("stalls", stats.Stalls);
            json.WriteNumber("flushes", stats.Flushes);
            json.WriteNumber("squashed", stats.Squashed);
            json.WriteNumber("forwards", stats.Forwards);
            json.WriteNumber("branches", stats.Branches);
            json.WriteNumber("mispredictions", stats.Mispredictions);
            json.WriteEndObject();

            json.WriteStartArray("cycles");
            foreach (var record in records)
            {
                Cycle(json, record, labels);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length) + "\n";
    }

    private static void Cycle(Utf8JsonWriter json, CycleRecord record, InstructionLabels labels)
    {
        json.WriteStartObject();
        json.WriteNumber("cycle", record.Cycle);

        json.WriteStartObject("stages");
        foreach (var stage in Enum.GetValues<Stage>())
        {
            var inside = record[stage];
            if (!inside.HasInstruction)
            {
                json.WriteNull(AsciiTrace.Name(stage));
                continue;
            }

            var line = labels.Describe(inside.Pc, inside.Raw);
            json.WriteStartObject(AsciiTrace.Name(stage));
            json.WriteNumber("seq", inside.Seq);
            json.WriteString("pc", Hex(inside.Pc));
            json.WriteString("raw", Hex(inside.Raw));
            json.WriteString("text", line.Operands.Length == 0 ? line.Mnemonic : line.Mnemonic + " " + line.Operands);
            json.WriteString("state", inside.State switch
            {
                Occupancy.Held => "held",
                Occupancy.Squashed => "squashed",
                _ => "normal",
            });
            json.WriteEndObject();
        }

        json.WriteEndObject();

        json.WriteStartArray("events");
        foreach (var item in record.Events)
        {
            Event(json, item, labels);
        }

        json.WriteEndArray();

        var wires = record.Wires;
        json.WriteStartObject("wires");
        json.WriteString("nextPc", Hex(wires.NextPc));
        json.WriteString("nextPcFrom", wires.NextPcFrom switch
        {
            NextPcFrom.Sequential => "pc+4",
            NextPcFrom.Predicted => "predictor",
            NextPcFrom.Decode => "ID",
            NextPcFrom.Execute => "EX",
            NextPcFrom.Memory => "MEM",
            _ => "held",
        });
        json.WriteString("decodeRs1", Hex(wires.DecodeRs1));
        json.WriteString("decodeRs2", Hex(wires.DecodeRs2));
        json.WriteString("executeRs1", Hex(wires.ExecuteRs1));
        json.WriteString("executeRs2", Hex(wires.ExecuteRs2));
        json.WriteString("aluA", Hex(wires.AluA));
        json.WriteString("aluB", Hex(wires.AluB));
        json.WriteString("aluOut", Hex(wires.AluOut));
        json.WriteString("memoryAlu", Hex(wires.MemoryAlu));
        json.WriteString("memoryRs2", Hex(wires.MemoryRs2));
        json.WriteEndObject();

        if (record.Commit is { } commit)
        {
            json.WriteStartObject("commit");
            Commit(json, commit, labels);
            json.WriteEndObject();
        }

        if (record.End is { } end)
        {
            json.WriteStartObject("end");
            Commit(json, end, labels);
            json.WriteEndObject();
        }

        json.WriteEndObject();
    }

    private static void Event(Utf8JsonWriter json, PipelineEvent item, InstructionLabels labels)
    {
        json.WriteStartObject();
        switch (item)
        {
            case ForwardEvent forward:
                json.WriteString("type", "forward");
                json.WriteNumber("seq", forward.Seq);
                json.WriteString("from", forward.From == ForwardSource.ExMem ? "EX/MEM" : "MEM/WB");
                json.WriteString("to", AsciiTrace.Name(forward.To));
                json.WriteString("operand", forward.Operand == Core.Pipeline.Operand.A ? "rs1" : "rs2");
                json.WriteString("register", labels.Register(forward.Register));
                json.WriteString("value", Hex(forward.Value));
                json.WriteNumber("producer", forward.Producer);
                break;
            case StallEvent stall:
                json.WriteString("type", "stall");
                json.WriteNumber("seq", stall.Seq);
                json.WriteString("cause", stall.Cause switch
                {
                    StallCause.LoadUse => "load-use",
                    StallCause.DataHazard => "data-hazard",
                    StallCause.BranchOperand => "branch-operand",
                    _ => "multi-cycle",
                });
                json.WriteString("stage", AsciiTrace.Name(stall.Stage));
                if (stall.Producer != 0)
                {
                    json.WriteString("register", labels.Register(stall.Register));
                    json.WriteNumber("producer", stall.Producer);
                    json.WriteString("producerStage", AsciiTrace.Name(stall.ProducerStage));
                }
                else
                {
                    json.WriteNumber("remaining", stall.Remaining);
                }

                break;
            case FlushEvent flush:
                json.WriteString("type", "flush");
                json.WriteNumber("seq", flush.Seq);
                json.WriteString("cause", flush.Cause switch
                {
                    FlushCause.Branch => "branch",
                    FlushCause.System => "system",
                    FlushCause.Trap => "trap",
                    _ => "stop",
                });
                json.WriteString("stage", AsciiTrace.Name(flush.Stage));
                json.WriteNumber("by", flush.By);
                break;
            case BranchEvent branch:
                json.WriteString("type", "branch");
                json.WriteNumber("seq", branch.Seq);
                json.WriteBoolean("taken", branch.Taken);
                json.WriteString("target", Hex(branch.Target));
                json.WriteBoolean("mispredicted", branch.Mispredicted);
                json.WriteString("resolvedIn", AsciiTrace.Name(branch.ResolvedIn));
                break;
            case RegWriteEvent write:
                json.WriteString("type", "reg-write");
                json.WriteNumber("seq", write.Seq);
                json.WriteString("register", labels.Register(write.Register));
                json.WriteString("value", Hex(write.Value));
                break;
            case MemReadEvent read:
                json.WriteString("type", "mem-read");
                json.WriteNumber("seq", read.Seq);
                json.WriteString("address", Hex(read.Address));
                json.WriteNumber("bytes", read.Bytes);
                json.WriteString("value", Hex(read.Value));
                break;
            case MemWriteEvent store:
                json.WriteString("type", "mem-write");
                json.WriteNumber("seq", store.Seq);
                json.WriteString("address", Hex(store.Address));
                json.WriteNumber("bytes", store.Bytes);
                json.WriteString("value", Hex(store.Value));
                break;
            case TrapEvent trap:
                json.WriteString("type", "trap");
                json.WriteNumber("seq", trap.Seq);
                json.WriteNumber("cause", trap.Cause);
                json.WriteString("value", Hex(trap.Value));
                json.WriteString("handler", Hex(trap.Handler));
                break;
            default:
                json.WriteString("type", "commit");
                json.WriteNumber("seq", item.Seq);
                break;
        }

        json.WriteEndObject();
    }

    private static void Commit(Utf8JsonWriter json, Commit commit, InstructionLabels labels)
    {
        json.WriteString("pc", Hex(commit.Pc));
        json.WriteString("nextPc", Hex(commit.NextPc));
        if (commit.WritesRegister)
        {
            json.WriteString("register", labels.Register(commit.Register));
            json.WriteString("value", Hex(commit.Value));
        }

        if (commit.WritesMemory)
        {
            json.WriteStartObject("store");
            json.WriteString("address", Hex(commit.StoreAddress));
            json.WriteNumber("bytes", commit.StoreBytes);
            json.WriteString("value", Hex(commit.StoreValue));
            json.WriteEndObject();
        }

        if (commit.Trapped)
        {
            json.WriteStartObject("trap");
            json.WriteNumber("cause", commit.Cause);
            json.WriteString("value", Hex(commit.TrapValue));
            json.WriteEndObject();
        }

        if (commit.Stop != StopReason.None)
        {
            json.WriteString("stop", commit.Stop switch
            {
                StopReason.Exit => "exit",
                StopReason.EndOfProgram => "end-of-program",
                StopReason.Breakpoint => "breakpoint",
                StopReason.Fault => "fault",
                _ => "tohost",
            });
            if (commit.Stop is StopReason.Exit or StopReason.Tohost)
            {
                json.WriteNumber("exitCode", commit.ExitCode);
            }

            if (commit.Message is { } message)
            {
                json.WriteString("message", message);
            }
        }
    }

    private static string Hex(uint value) => value.ToString("x8", CultureInfo.InvariantCulture);
}
