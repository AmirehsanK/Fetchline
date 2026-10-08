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

The command line works; the browser playground is not built yet.

```bash
dotnet run --project src/Fetchline.Cli -- trace examples/load-use.s
```

```
                          1    2    3    4    5    6    7    8
 0x00 lw   x4, 0(x2)      IF   ID   EX   MEM  WB
 0x04 add  x5, x4, x6          IF   ID   ID   EX   MEM  WB
 0x08 sub  x7, x5, x4               IF   IF   ID   EX   MEM  WB

 c3  stall    load-use: add (ID) needs x4; lw (EX) has it only after MEM
 c5  forward  MEM/WB -> EX.A   x4 from lw
 c6  forward  EX/MEM -> EX.A   x5 from add
 3 instructions, 8 cycles, CPI 2.67, 1 stall, 2 forwards
```

That is the five-stage pipeline: forwarding, the load-use stall, branches decided in EX, and
system instructions and traps taken at the commit point. Underneath it is a reference machine
that runs one instruction at a time and passes the official RISC-V tests (64 of 66; the other
two need features a machine-mode core does not have). The pipeline is checked against it
instruction by instruction.

```bash
dotnet run --project src/Fetchline.Cli -- run examples/fib.s
```

```
0 1 1 2 3 5 8 13 21 34
```

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

`asm -o program.elf` writes an ELF executable, and `dis` and `run` read one back. A mistake in the
source is reported with its line, a caret, and a suggestion when there is one:

```
prog.s:2:5: error: unknown instruction 'adid'
      adid a0, a0, 1
      ^~~~
  did you mean 'addi'?
```

A program that does something it cannot do is stopped with a sentence, not a wrong answer:

```
fetchline: the program stopped: a store to 0x00000000, which is in 'text' and cannot be written, at pc 0x00000004
  prog.s:3: sw   a0, 0(zero)
```

## Licence

MIT. See [LICENSE](LICENSE).
