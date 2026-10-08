# Fetchline

A RISC-V CPU, an assembler and a pipeline visualizer, written in C#.

Fetchline runs RV32IM programs on a five-stage pipeline and explains what the pipeline does with
them: every stall, flush and forward is an event with a cause, drawn as the textbook diagram and
written as a sentence. The same engine runs in the terminal, in the tests and in the browser.

**Status: under construction.** The milestones and what each one has to prove are in
[docs/SPEC.md](docs/SPEC.md); the table there is ticked as each section lands.

## Build

It needs the .NET 10 SDK and nothing else.

```bash
dotnet build
dotnet test
```

## Licence

MIT. See [LICENSE](LICENSE).
