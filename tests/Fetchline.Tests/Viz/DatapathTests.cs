using Fetchline.Core.Isa;
using Fetchline.Core.Pipeline;
using Fetchline.Tests.Support;
using Fetchline.Viz;
using Fetchline.Viz.Datapath;
using Fetchline.Viz.Explain;

namespace Fetchline.Tests.Viz;

/// <summary>
/// The datapath as data. Half of this holds the model to the rules of a drawing, since nobody
/// placed its wires by eye; the other half holds what is lit in a cycle to what happened in it.
/// </summary>
public class DatapathTests
{
    /// <summary>Every way the pipeline can be built, the ones that give wrong answers included.</summary>
    private static IEnumerable<PipelineConfig> EveryWay() =>
        Configurations.Correct.Concat(Configurations.Correct.Select(config => config with { Hazards = HazardHandling.Off })).Distinct();

    private static string Hex(uint value) => $"0x{value:x8}";

    /// <summary>The datapath in each cycle of a run, from cycle one.</summary>
    private static (List<DatapathView> Views, Session Session) Run(string source, PipelineConfig? config = null)
    {
        var session = new Session(source, config);
        var model = DatapathModel.For(session.Config);
        var views = new List<DatapathView>();
        while (session.CanStep && session.Stopped != Core.Trace.StopReason.Breakpoint && views.Count < 1000)
        {
            session.Step();
            views.Add(DatapathView.Of(model, session.Record));
        }

        return (views, session);
    }

    private static string[] Lit(DatapathView view) =>
        [.. view.Wires.Where(reading => reading.Active).Select(reading => reading.Wire.Id).Order(StringComparer.Ordinal)];

    [Fact]
    public void EveryWireRunsFromOnePartToAnotherInLevelAndUprightStretches()
    {
        foreach (var config in EveryWay())
        {
            var model = DatapathModel.For(config);
            var name = Configurations.Name(config);
            Assert.Equal(model.Wires.Count, model.Wires.Select(wire => wire.Id).Distinct().Count());

            foreach (var wire in model.Wires)
            {
                var what = $"{wire.Id} with {name}";
                Assert.True(model.Has(wire.From) && model.Has(wire.To), what);
                Assert.True(wire.Points.Count >= 2, what);
                Assert.True(model.Part(wire.From).HasOnOutline(wire.Points[0]), $"{what} does not begin on {wire.From}");
                Assert.True(model.Part(wire.To).HasOnOutline(wire.Points[^1]), $"{what} does not end on {wire.To}");

                for (var i = 1; i < wire.Points.Count; i++)
                {
                    var (a, b) = (wire.Points[i - 1], wire.Points[i]);
                    Assert.True((a.X == b.X) != (a.Y == b.Y), $"{what}: the stretch to corner {i} is not level or upright");
                    Assert.True(b.X is > 0 and < DatapathModel.Width && b.Y is > 0 and < DatapathModel.Height, what);

                    // A wire goes round the parts, never through one: not even the two it joins.
                    foreach (var part in model.Parts)
                    {
                        var through = Math.Max(a.X, b.X) > part.X && Math.Min(a.X, b.X) < part.Right
                            && Math.Max(a.Y, b.Y) > part.Y && Math.Min(a.Y, b.Y) < part.Bottom;
                        Assert.False(through, $"{what} goes through {part.Id}");
                    }
                }
            }
        }
    }

    [Fact]
    public void ThePartsKeepToTheirStagesAndOutOfEachOthersWay()
    {
        foreach (var config in EveryWay())
        {
            var model = DatapathModel.For(config);
            var parts = model.Parts.ToList();
            Assert.Equal(4, parts.Count(part => part.Kind == PartKind.Latch));

            foreach (var part in parts)
            {
                Assert.True(part.X > 0 && part.Y > 0 && part.Right < DatapathModel.Width && part.Bottom < DatapathModel.Height, part.Id);
                if (part.Kind != PartKind.Latch)
                {
                    var (from, to) = model.Column(part.Stage);
                    Assert.True(part.X > from && part.Right < to, $"{part.Id} is not within {part.Stage}");
                    Assert.NotEqual(string.Empty, part.Kind == PartKind.Mux ? "mux" : part.Label);
                }

                // Parts stand clear of each other, with room for a wire to pass between.
                foreach (var other in parts.Where(other => !ReferenceEquals(other, part)))
                {
                    var apart = part.X >= other.Right + 4 || other.X >= part.Right + 4
                        || part.Y >= other.Bottom + 4 || other.Y >= part.Bottom + 4;
                    Assert.True(apart, $"{part.Id} and {other.Id}");
                }
            }
        }
    }

    [Fact]
    public void TheDatapathIsThatOfThePipelineAsItIsBuilt()
    {
        static (bool Forward, bool Branch, bool Compare, bool Predictor, bool Hazard) Of(PipelineConfig config)
        {
            var model = DatapathModel.For(config);
            Assert.Equal(model.Has("forward"), model.Has("fwd-a") && model.Has("fwd-b"));
            return (model.Has("forward"), model.Has("branch"), model.Has("cmp"), model.Has("predictor"), model.Has("hazard"));
        }

        Assert.Equal((true, true, false, false, true), Of(PipelineConfig.Default));
        Assert.Equal((false, true, false, false, true), Of(new PipelineConfig { Hazards = HazardHandling.StallOnly }));
        Assert.Equal((true, false, true, false, true), Of(new PipelineConfig { Branches = BranchDecision.Decode }));
        Assert.Equal((true, true, false, true, true), Of(new PipelineConfig { Predictor = Predictor.BackwardTaken }));

        // With nobody watching there is no hazard unit, unless a multiplier needs the pipeline held.
        Assert.Equal((false, true, false, false, false), Of(new PipelineConfig { Hazards = HazardHandling.Off }));
        Assert.Equal((false, true, false, false, true), Of(new PipelineConfig { Hazards = HazardHandling.Off, MulDivCycles = 3 }));

        // A value can only be forwarded to the branch logic in ID when there is forwarding.
        var early = new PipelineConfig { Branches = BranchDecision.Decode };
        Assert.Contains(DatapathModel.For(early).Wires, wire => wire.Id == "cmp-forward");
        Assert.DoesNotContain(
            DatapathModel.For(early with { Hazards = HazardHandling.StallOnly }).Wires, wire => wire.Id == "cmp-forward");
    }

    [Fact]
    public void AtResetNothingIsInUse()
    {
        var view = DatapathView.Of(DatapathModel.For(PipelineConfig.Default), record: null);

        Assert.Empty(Lit(view));
        Assert.DoesNotContain(view.Model.Parts, part => view.IsWorking(part.Id));
        Assert.Equal("pc + 4: not used this cycle", view["add-mux"].Describe(EnglishMessages.Instance));
    }

    [Fact]
    public void AnInstructionLightsItsOwnPathThroughEachStage()
    {
        var (views, session) = Run("li t0, 5\naddi t1, t0, 1\nsw t1, 0(sp)\nlw t2, 0(sp)\n");
        var text = session.Program!.Text!.Address;
        var sp = Hex(0x7FFF_FFF0);

        // Cycle 1: only fetch is at work. The counter goes to memory and to the adder, and the
        // adder's answer is chosen for the next address.
        Assert.Equal(["add-mux", "imem-ifid", "mux-pc", "pc-add", "pc-ifid", "pc-imem"], Lit(views[0]));
        Assert.Equal((Hex(text), Hex(text + 4)), (views[0]["pc-imem"].Value, views[0]["add-mux"].Value));
        Assert.True(views[0].IsWorking("imem") && views[0].IsWorking("pc-add") && !views[0].IsWorking("regs"));

        // Cycle 2: li is in ID. It reads x0 and has an immediate; it has no rs2.
        Assert.Equal(("zero", Hex(0), Hex(5)), (views[1]["id-rs1"].Value, views[1]["id-a"].Value, views[1]["id-imm"].Value));
        Assert.False(views[1]["id-rs2"].Active || views[1]["id-b"].Active);

        // Cycle 3: li is in EX, and the ALU adds its immediate to x0.
        Assert.Equal((Hex(0), Hex(5), Hex(5)), (views[2]["alu-a"].Value, views[2]["alu-b"].Value, views[2]["alu-out"].Value));
        Assert.True(views[2]["ex-rs1"].Active && views[2]["ex-imm"].Active && !views[2]["ex-pc"].Active);
        Assert.False(views[2]["forward-a"].Active || views[2].IsWorking("forward"));
        Assert.Equal("t0", views[2]["id-rs1"].Value);

        // Cycle 4: addi needs t0, which is still on its way: the forwarding unit takes it from
        // EX/MEM, and what ID/EX holds for it is not used.
        Assert.Equal(Hex(5), views[3]["fwd-exmem-a"].Value);
        Assert.True(views[3]["forward-a"].Active && views[3].IsWorking("forward") && !views[3]["ex-rs1"].Active);
        Assert.Equal((Hex(5), Hex(6)), (views[3]["fwd-a-out"].Value, views[3]["alu-out"].Value));
        Assert.Equal("forwarded value = 0x00000005", views[3]["fwd-exmem-a"].Describe(EnglishMessages.Instance));
        Assert.Equal("select the forwarded value", views[3]["forward-a"].Describe(EnglishMessages.Instance));

        // li is in MEM, which it has no use for: its result goes round the memory.
        Assert.Equal(Hex(5), views[3]["mem-result"].Value);
        Assert.False(views[3]["mem-address"].Active || views[3].IsWorking("dmem"));

        // Cycle 5: li writes t0. The store is in EX: its address is sp, and what it stores is
        // forwarded from the addi ahead of it.
        Assert.Equal((Hex(5), Hex(5)), (views[4]["wb-result"].Value, views[4]["wb-write"].Value));
        Assert.True(views[4].IsWorking("regs") && !views[4]["wb-mem"].Active);
        Assert.Equal((Hex(6), Hex(6), sp), (views[4]["fwd-exmem-b"].Value, views[4]["store-data"].Value, views[4]["alu-out"].Value));
        Assert.False(views[4]["fwd-b-out"].Active);      // the ALU's second operand is the offset

        // Cycle 6: the store writes; cycle 7: the load reads the same word back; cycle 8: it is
        // written to t2 from the memory side of the last multiplexer.
        Assert.Equal((sp, Hex(6)), (views[5]["mem-address"].Value, views[5]["mem-data"].Value));
        Assert.False(views[5]["mem-read"].Active || views[5]["mem-result"].Active);
        Assert.Equal((sp, Hex(6)), (views[6]["mem-address"].Value, views[6]["mem-read"].Value));
        Assert.Equal((Hex(6), Hex(6)), (views[7]["wb-mem"].Value, views[7]["wb-write"].Value));
        Assert.False(views[7]["wb-result"].Active);
    }

    [Fact]
    public void AStallHoldsTheCounterAndABranchSendsItSomewhereElse()
    {
        var (views, session) = Run("lw t0, 0(sp)\naddi t1, t0, 1\nbeqz zero, on\nnop\nnop\non: ebreak\n");
        var text = session.Program!.Text!.Address;

        // Cycle 3: the load-use stall. The hazard unit holds the counter and IF/ID; nothing new
        // is chosen for the counter, and the addi in ID goes nowhere.
        Assert.True(views[2]["hazard-pc"].Active && views[2]["hazard-ifid"].Active && views[2].IsWorking("hazard"));
        Assert.False(views[2]["mux-pc"].Active || views[2]["add-mux"].Active || views[2]["id-a"].Active);
        Assert.True(views[2]["id-rs1"].Active);
        Assert.Equal("hold", views[2]["hazard-pc"].Describe(EnglishMessages.Instance));
        Assert.False(views[1].IsWorking("hazard") || views[3].IsWorking("hazard"));

        // Cycle 5: the addi is in EX at last, and the load's value comes to it from MEM/WB.
        Assert.True(views[4]["fwd-memwb-a"].Active && views[4]["wb-mem"].Active);

        // The branch is decided in EX: its operands go to the branch logic, which compares them
        // and has an adder of its own for where the branch leads, and the counter is sent there
        // instead of straight on.
        var decided = views.Single(view => view["redirect-ex"].Active);
        Assert.Equal(Hex(text + 20), decided["redirect-ex"].Value);
        Assert.Equal(Hex(text + 20), decided["mux-pc"].Value);
        Assert.True(decided.IsWorking("branch") && decided["branch-a"].Active && decided["branch-b"].Active);
        Assert.False(decided["add-mux"].Active);

        // The ebreak takes effect at the commit point.
        Assert.True(views[^2]["redirect-mem"].Active && views[^2].IsWorking("commit"));
        Assert.DoesNotContain(views.Take(views.Count - 2), view => view.IsWorking("commit"));
    }

    [Fact]
    public void BuiltAnotherWayTheSameProgramLightsOtherParts()
    {
        const string Source = "li t0, 1\nbnez t0, on\nnop\non: add t1, t0, t0\n";

        // Decided in ID with forwarding: the branch waits a cycle for t0, then takes it from
        // EX/MEM, and the redirect comes from ID.
        var (early, _) = Run(Source, new PipelineConfig { Branches = BranchDecision.Decode });
        Assert.True(early[2].IsWorking("hazard") && !early[2].IsWorking("cmp"));
        Assert.True(early[3].IsWorking("cmp") && early[3]["redirect-id"].Active);
        Assert.Equal((Hex(1), false), (early[3]["cmp-forward"].Value, early[3]["cmp-a"].Active));
        Assert.DoesNotContain(early[3].Wires, reading => reading.Wire.Id == "redirect-ex");

        // Stalling only: no forwarding paths to light, and the operands go straight to the
        // multiplexers that choose the ALU's inputs.
        var (stalled, _) = Run(Source, new PipelineConfig { Hazards = HazardHandling.StallOnly });
        Assert.DoesNotContain(stalled, view => view.Wires.Any(reading => reading.Wire.Signal == Signal.Forwarded));
        var add = stalled.Last(view => view["ex-rs1"].Active && view["ex-rs2"].Active);
        Assert.Equal((Hex(1), Hex(1), Hex(2)), (add["ex-rs1"].Value, add["ex-rs2"].Value, add["alu-out"].Value));

        // With no forwarding multiplexers in the way, a branch's operands go from ID/EX straight
        // to the branch logic.
        var decided = stalled.Single(view => view["redirect-ex"].Active);
        Assert.True(decided["branch-a"].Active && decided["branch-b"].Active && decided.IsWorking("branch"));

        // A predictor that has learnt the loop sends fetch round it.
        var (loop, _) = Run("li t0, 4\nagain: addi t0, t0, -1\nbnez t0, again\n", new PipelineConfig { Predictor = Predictor.TwoBit });
        var guessed = loop.First(view => view["pred-mux"].Active);
        Assert.True(guessed.IsWorking("predictor") && !guessed["add-mux"].Active);
        Assert.Equal(guessed["pred-mux"].Value, guessed["mux-pc"].Value);
    }

    [Fact]
    public void RegistersOnTheWiresAreNamedTheWayAsked()
    {
        var session = new Session("add x7, x5, x6\n");
        session.Seek(2);
        var model = DatapathModel.For(session.Config);

        Assert.Equal(("t0", "t1"), (DatapathView.Of(model, session.Record)["id-rs1"].Value, DatapathView.Of(model, session.Record)["id-rs2"].Value));
        Assert.Equal("x5", DatapathView.Of(model, session.Record, RegisterStyle.Numeric)["id-rs1"].Value);
    }

    [Fact]
    public void EveryWireIsLitByOneOfTheExamplesOrByATrap()
    {
        // A wire that nothing ever lights is a wire drawn for nothing, or one the view forgot.
        var sources = Directory.GetFiles(Repo.PathOf("examples"), "*.s").Select(File.ReadAllText)
            .Append("la t0, on\njalr t0\nnop\non: csrr t1, mscratch\nlw t2, 1(zero)\n")
            .ToList();
        var ways = new[]
        {
            PipelineConfig.Default,
            new PipelineConfig { Hazards = HazardHandling.StallOnly, MulDivCycles = 3 },
            new PipelineConfig { Branches = BranchDecision.Decode, Predictor = Predictor.TwoBit },
            new PipelineConfig { Branches = BranchDecision.Decode, Hazards = HazardHandling.StallOnly, Predictor = Predictor.BackwardTaken },
        };

        foreach (var config in ways)
        {
            var model = DatapathModel.For(config);
            var lit = new HashSet<string>();
            var working = new HashSet<string>();
            foreach (var source in sources)
            {
                var session = new Session(source, config);
                for (var cycle = 0; cycle < 3000 && session.CanStep; cycle++)
                {
                    session.Step();
                    var view = DatapathView.Of(model, session.Record);
                    lit.UnionWith(Lit(view));
                    working.UnionWith(model.Parts.Where(part => view.IsWorking(part.Id)).Select(part => part.Id));
                }
            }

            var name = Configurations.Name(config);
            Assert.True(lit.SetEquals(model.Wires.Select(wire => wire.Id)),
                $"never lit with {name}: {string.Join(", ", model.Wires.Select(wire => wire.Id).Except(lit))}");
            Assert.True(working.SetEquals(model.Parts.Select(part => part.Id)),
                $"never at work with {name}: {string.Join(", ", model.Parts.Select(part => part.Id).Except(working))}");
        }
    }
}
