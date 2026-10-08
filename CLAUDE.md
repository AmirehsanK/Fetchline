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
- Package versions are central, in `Directory.Packages.props`, and exact. Every package the build
  uses was already in the NuGet cache; the connection is slow and its DNS is intermittent, so a
  restore or a push that hangs is usually the network. Retry it before changing anything.
- Python is not on the PATH. A one-off script is a `.cs` file run with `dotnet run file.cs`, or
  PowerShell.
- `.gitattributes` makes every text file LF. The machine-wide `core.autocrlf=true` does not apply
  inside this repository because of it.

## Commands

```bash
dotnet build                      # every project; warnings are errors
dotnet test                       # every test project
dotnet run --project src/Fetchline.Cli -- <command>   # the command line, from source
```

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
