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

## Try it

What works so far is the assembler and the disassembler:

```bash
dotnet run --project src/Fetchline.Cli -- asm examples/sum.s --listing
```

```
00000000 <main>:
00000000:  00000513  li      a0, 0
00000004:  00100293  li      t0, 1
00000008:  00a00313  li      t1, 10

0000000c <loop>:
0000000c:  00550533  add     a0, a0, t0
00000010:  00128293  addi    t0, t0, 1
00000014:  fe535ce3  bge     t1, t0, loop     # ble t0, t1, loop
...
```

`asm -o program.elf` writes an ELF executable, and `dis` reads one back. A mistake in the source
is reported with its line, a caret, and a suggestion when there is one:

```
prog.s:2:5: error: unknown instruction 'adid'
      adid a0, a0, 1
      ^~~~
  did you mean 'addi'?
```

## Licence

MIT. See [LICENSE](LICENSE).
