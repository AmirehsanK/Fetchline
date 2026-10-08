# Working notes for Claude

## The controlling document

`docs/SPEC.md` is the specification: scope, the ISA, the assembler, both machines, the visualizer
and the milestones. Follow it. Where the code and the spec disagree, that is a bug in one of them:
resolve it explicitly, do not silently diverge. `docs/CPU.md` explains each mechanism and how it
was validated; it is written alongside the engine, one mechanism at a time.

The repository is <https://github.com/AmirehsanK/Fetchline> (private since it was created on
8 October 2026; making it public is the owner's call).

## Environment facts

- Windows 11, .NET SDK 10.0.303. The shell is PowerShell; Git Bash is there for POSIX scripts.
- **There is no RISC-V toolchain here**, and WSL and Docker are not running. Anything that needs
  `riscv64-unknown-elf-gcc` or `objdump` is done by `.github/workflows/vectors.yml` on a GitHub
  runner and committed under `tests/vectors/` (see `docs/SPEC.md` 11.1).
- Package versions are central, in `Directory.Packages.props`, and exact. The connection is slow
  and its DNS is intermittent, so a restore or a push that hangs is usually the network. Retry it
  before changing anything. The Blazor WebAssembly packages and the browser runtime pack
  (10.0.11) were fetched from nuget.org on 8 October 2026 and are in the NuGet cache now; the
  `wasm-tools` workload is not installed, and nothing needs it.
- Python is not on the PATH. A one-off script is a `.cs` file run with `dotnet run file.cs`, or
  PowerShell.
- `.gitattributes` makes every text file LF. The machine-wide `core.autocrlf=true` does not apply
  inside this repository because of it.

## Commands

```bash
dotnet build                      # every project; warnings are errors
dotnet test                       # every test project
dotnet run --project src/Fetchline.Cli -- <command>   # the command line, from source
dotnet run --project src/Fetchline.Web --launch-profile http   # the playground, at http://localhost:5195
dotnet run -c Release --project bench/Fetchline.Benchmarks -- --filter '*'   # the figures in docs/CPU.md
```

The playground is also the preview configuration `fetchline-web` in `D:\Git\.claude\launch.json`
(outside this repository). Look at a change to it in a real browser before calling it done; the
tests cover what is drawn, not how it looks. The development server does not pick up a rebuilt
assembly: stop it, build, and start it again.

```bash
pwsh tools/site-size.ps1                                    # publish to artifacts/site and add up the download
dotnet run tools/serve.cs -- ../artifacts/site/wwwroot 5196   # serve what was published (preview: fetchline-built)
```

`tools/serve.cs` is a single-file program, and `dotnet run` starts it in its own folder, which
is why the path it is given begins with `..`. The download figures in `docs/CPU.md` 8.1 come
from the first of these two; measure again after anything that changes what is shipped.

`dotnet test` takes about twenty seconds here. Most of that is `EveryConfigurationTests`, which
runs 2,000 random programs on all 64 correct configurations, in parallel; a failure there prints
the number of the program, its seed and the switches, which is everything needed to replay it
with `ProgramGenerator` and `fetchline trace`.

The diagrams under `tests/golden` are compared exactly. After a deliberate change to what a trace
looks like, set `FETCHLINE_UPDATE_GOLDEN=1`, run the tests once, and read every file that changed
before committing it: they are the pictures the tool draws.

A PowerShell command that pipes a long-running `dotnet` into `Select-Object -First N` kills it
when N lines have arrived. That is how a benchmark run was once cut short; send the output to a
file and read the file instead.

## Conventions

- Comments explain *why*, especially where the design departs from the obvious approach.
- A new CPU behaviour needs a test that shows it and an example that teaches it. Realism has no
  natural end; that pairing is the boundary.
- A figure quoted in `README.md` or `docs/CPU.md` comes from a test or the benchmark. When the
  engine changes, measure it again before trusting the sentence.
- Numbers are written as hex or invariant decimal. Nothing in the engine formats for a culture,
  and `InvariantGlobalization` is on.
- A randomised test uses the seeded generator in the test project and prints its seed when it
  fails, so the failure can be replayed.
- Technical proper nouns stay English in the Persian UI: stage names, register names, mnemonics.
- `Fetchline.Web` draws and listens; what there is to draw is decided in `Fetchline.Viz`, where it
  can be tested without a browser. The look is one stylesheet, `wwwroot/css/display.css`, whose
  first block holds the tokens. `DisplayStylesheetTests` reads it and fails if a phosphor's text
  drops below 4.5 to 1, if the plain switch leaves an effect on, or if a font or script is
  fetched from another site.

## Load-bearing decisions that look optional but are not

Each has a test.

- **`Fetchline.Core` has no package references, no I/O, no clock, no unseeded randomness.**
  `CorePurityTests` reads the compiled assembly and fails on the first reference to any of them.
  Do not add an exception to its lists to make something compile; pass the data in instead.
  One trap: a method that uses `yield return` makes the compiler emit a state machine that reads
  `Environment.CurrentManagedThreadId`, and the test rejects it. In the engine, build a list.
- **Nothing outside the instruction table knows an opcode.** The decoder, the encoder, the
  assembler and the disassembler all read `Isa/InstructionSet`, and that table is checked against
  the official `riscv-opcodes` files.
- **Both machines call the same `Exec` functions.** The official tests prove the semantics on the
  reference machine; lockstep then proves the pipeline. A fix that only one machine gets is a bug.
- **A hazard is an event the engine emits, never something the UI infers.** Diagrams, counters and
  sentences are derived from the record stream, so they cannot disagree with the engine.
- **A dependency counts only if the instruction uses that register.** `lui` has no `rs1` and an
  I-type has no `rs2`, whatever bits sit in those fields.
- **MEM is the commit point.** System instructions and traps take effect there and squash what is
  younger, so nothing irreversible happens on a wrong path.
- **A share link is untrusted input.** Decoding caps the decompressed size and validates before
  anything renders or runs.

## Commit and publish

The owner asked on 8 October 2026 for each milestone to be split into smaller sections and for
each section to be committed and pushed. `docs/SPEC.md` section 10 lists the sections; tick a box
in the commit that finishes it. Commit only what builds and passes `dotnet test`.

Switching on Pages, making the repository public, changing its settings and downloading a font
are outward-facing: ask first.
