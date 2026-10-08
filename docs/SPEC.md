# Fetchline — specification

Fetch is the first of the five stages every instruction passes through. The name pairs with
[Loadline](https://github.com/AmirehsanK/Loadline).

## 1. Purpose

A RISC-V CPU, an assembler and a pipeline visualizer: write assembly, run it on a five-stage
pipeline, and see every hazard, stall and flush drawn and explained.

The genre is crowded at the student level and thin at the verified level. Ripes and QtRvSim are
desktop programs carried to the browser; WebRISC-V needs a server; Venus, RARS and emulsiV have no
pipeline; most small visualizers on GitHub implement ten to fifteen instructions and check none of
them. This project is a portfolio and learning piece, so its value is in execution:

1. **A real ISA, verified.** RV32IM with CSRs and traps, passing the official `riscv-tests` on
   both the reference interpreter and the pipeline, in every hazard configuration.
2. **It explains, not only shows.** Every stall, flush and forward is a typed event with a cause,
   a producer, a consumer and a register, drawn as the textbook diagram and written as a sentence.
3. **What-if switches.** Forwarding on or off, branch decided in EX or ID, four predictors, and a
   table of cycles and CPI for each. With the hazard unit off the program computes a wrong answer
   and the tool points at the first wrong value.
4. **One engine, three front ends:** command line, tests and browser, all C#.

Not goals: a fast emulator, a full system (no operating system boots on it), accounts, a backend.

## 2. Decisions

| Topic | Choice | Reason |
|---|---|---|
| Language | C# on .NET 10 | It is the unusual part on a .NET profile; LTS and installed |
| ISA | RV32I + M + Zicsr + Zifencei, machine mode only | An open specification with official tests, so correctness is shown, not claimed; it is what the textbooks teach; 32-bit values fit a diagram |
| Core library | `Fetchline.Core`: no package dependencies, no I/O, no clock, no unseeded randomness, trimmable | It must behave the same in tests, the command line and the browser. `CorePurityTests` enforces it |
| One instruction table | Mnemonic, format, match, mask, operand pattern, control signals. The decoder, encoder, assembler, disassembler and the in-app reference all read it | One place to be wrong, and it is checked against `riscv-opcodes` |
| Shared semantics | The reference machine and the pipeline call the same `Exec` functions for arithmetic, branches and memory | The official tests prove the semantics; lockstep then proves the pipeline's plumbing |
| Pipeline kernel | Two-phase: every stage computes from the current latches (in the order WB, MEM, EX, ID, IF), then all latches update together | Models same-cycle signals (register write before read, forwarding, stall, flush) without ordering bugs |
| Events, not inference | A stall, flush or forward is emitted by the hazard logic with its cause. Diagrams and statistics are derived from events | The UI can never disagree with the engine |
| Determinism | Same program and configuration give the same record stream, and a test hashes it | Step back is replay; a share link reproduces a cycle |
| Memory map | Text at `0x0000_0000`, data at `0x1000_0000`, stack top `0x7FFF_FFF0`; sparse 4 KB pages | Matches Ripes and Venus, so course programs run unchanged; an ELF keeps its own addresses (`0x8000_0000` for the tests) |
| System calls | `a7` selects: 1 print int, 4 print string, 10 exit, 11 print char, 64 write, 93 exit(code) | RARS and Ripes numbering, plus the Linux numbers |
| Web UI | Blazor WebAssembly, standalone (the default; settled before M7) | The whole repository is C#, and the UI calls the engine directly |
| Tests | xunit.v3; a seeded generator of our own for random programs (a failure prints its seed) | No property-testing package is needed for a few dozen lines |
| Hosting | GitHub Pages, built by GitHub Actions (M9) | Free and static |
| Docs | This file controls; `docs/CPU.md` says how each mechanism works and how it was validated; `CLAUDE.md` holds conventions | The Loadline arrangement |

## 3. Repository layout

```
Fetchline.slnx  Directory.Build.props  Directory.Packages.props  global.json
CLAUDE.md                 conventions and load-bearing decisions
docs/SPEC.md              this file
docs/CPU.md               how each mechanism works and how it was validated
src/Fetchline.Core/       the engine
  Isa/                    registers, bit fields, the instruction table, decoder, encoder, disassembler
  Asm/                    lexer, expressions, parser, layout, encoding, pseudo-instructions, diagnostics
  Elf/                    ELF32 reader and writer
  Machine/                memory, CSRs, Exec, the reference machine, the host and bare environments
  Pipeline/               latches, stages, hazard unit, predictors, cycle records, lockstep
  Trace/                  commit records and their hash
src/Fetchline.Viz/        staircase layout, datapath highlights, explanations, ASCII / JSON / Kanata, share codec
src/Fetchline.Cli/        fetchline asm | dis | run | trace | compare | test
src/Fetchline.Web/        the playground (Blazor WebAssembly)
tests/Fetchline.Tests/        unit, golden, property and fuzz tests
tests/Fetchline.Conformance/  the official ELF files, on both machines
tests/vectors/            riscv-tests ELF and objdump files, riscv-opcodes files, PROVENANCE.md
tests/golden/             ASCII diagrams and Kanata files
bench/Fetchline.Benchmarks/
examples/*.s
.github/workflows/        ci.yml, vectors.yml, pages.yml
```

A project is added to the solution when its first real code lands, not before.

## 4. The instruction set

RV32I, M, Zicsr and Zifencei, plus `mret` and `wfi`: 57 instructions. Machine mode only.

- **One table** (`Isa/InstructionSet`) holds, for each instruction: mnemonic, encoding format,
  match and mask, the operand pattern its assembly takes, which of `rs1`, `rs2` and `rd` it really
  uses, and its control signals. Nothing else in the code knows an opcode.
- **Decoding** returns the instruction and its fields with the immediate already sign-extended.
  A word no row matches is `illegal`; so are the RV64 shift encodings (bit 25 set), which RV32
  reserves.
- **Encoding** is the inverse and rejects a field that does not fit (an immediate out of range, a
  branch offset that is odd) with a reason the assembler can show.
- **The cross-check.** `riscv/riscv-opcodes` stores every instruction as a line such as
  `lui rd imm20 6..2=0x0D 1..0=3`. A test parses the six files that cover this ISA and requires
  every match and mask in the table to equal the official one.

## 5. Assembler

A GNU `as` compatible subset, so programs written for Ripes, Venus or RARS mostly assemble
unchanged.

- Line-oriented lexer and parser; every token keeps its line and column. Errors are collected,
  not thrown, and each has a caret position and a "did you mean" for mnemonics and registers.
- Labels, numeric local labels (`1:` with `1b` and `1f`), `.equ` / `.set`, and expressions with
  `+ - * / % << >> & | ^ ~`, parentheses, and `%hi`, `%lo`, `%pcrel_hi`, `%pcrel_lo`.
- Directives: `.text .data .rodata .bss .section .globl .global .align .p2align .balign .byte
  .half .2byte .word .4byte .long .ascii .asciz .string .zero .space .equ .set`; `.option`,
  `.type`, `.size`, `.file`, `.attribute` and `.ident` are accepted and ignored.
- The standard pseudo-instructions: `nop unimp li la lla mv not neg seqz snez sltz sgtz beqz bnez blez
  bgez bltz bgtz bgt ble bgtu bleu j jal jr jalr ret call tail csrr csrw csrs csrc csrwi csrsi
  csrci rdcycle rdinstret rdcycleh rdinstreth` (`jal label` and `jalr rs` are the short forms).
- **`li` has a fixed size by the end of pass 1.** If its value is known there: one `addi` when it
  fits 12 signed bits, one `lui` when its low 12 bits are zero, otherwise `lui` + `addi`. If the
  value depends on a symbol defined later, it is always `lui` + `addi`.
- **Sections.** `.text` starts at `0x0000_0000`. `.data` starts at `0x1000_0000`, followed by
  `.rodata` and then `.bss`, each aligned to 16 bytes. `.section` accepts the names that map onto
  those four (`.text.*`, `.data.*`, `.sdata`, `.rodata.*`, `.srodata`, `.bss.*`, `.sbss`).
- **Entry point:** the symbol `_start` if it is defined, otherwise the start of `.text`.
- A number where a branch or jump expects a label is an absolute address, as in GNU `as`.
- Output is a `Program`: segments, symbols, and a source map in both directions that also records
  which machine instructions a pseudo-instruction expanded into. The UI shows both.
- A disassembler (canonical and alias forms) and an ELF32 reader and writer.

## 6. Reference machine

A plain interpreter: fetch, decode through a cache of decoded instructions, execute, return a
commit record (`pc`, instruction, register written, memory written, trap, next `pc`).

### 6.1 Environments

- **Host** (the playground and `run`). `ecall` is a system call and `ebreak` pauses. `sp` starts at
  `0x7FFF_FFF0` and `gp` at `0x1000_0000`. Text is read-only. A fetch at the address just past the
  last instruction ends the program cleanly, and `ra` starts at that address, so a program whose
  entry function ends with `ret` ends cleanly too. An access outside the mapped regions (the
  program's segments, 256 MB from the data base for the heap, and 1 MB of stack) stops with a
  readable message that names the address and the `pc`.
- **Bare** (chosen when an ELF has a `tohost` symbol). `ecall` and `ebreak` trap to `mtvec`,
  memory is flat and writable everywhere, registers start at zero, and a non-zero write to
  `tohost` ends the run with the test's verdict: 1 is a pass, `(n << 1) | 1` is test `n` failing.

System calls in the host environment, selected by `a7`:

| `a7` | Call | Effect |
|---|---|---|
| 1 | print int | writes `a0` as a signed decimal |
| 4 | print string | writes the NUL-terminated string at `a0` |
| 10 | exit | ends the program with code 0 |
| 11 | print char | writes the low byte of `a0` |
| 64 | write | writes `a2` bytes from `a1` when `a0` is 1 or 2, and returns the count in `a0` |
| 93 | exit(code) | ends the program with code `a0` |

Any other number stops the program with a message.

### 6.2 CSRs and traps

`mstatus` (MIE, MPIE; MPP fixed to machine), `misa`, `mtvec` (direct), `mepc`, `mcause`, `mtval`,
`mscratch`, `mie`, `mip`, `mhartid` and the ID registers as zero, `mcycle` and `minstret` with
their high halves and their read-only user aliases. Any other CSR raises an illegal-instruction
trap, which is what lets the official tests' reset code skip the features this core lacks. A
write to a read-only CSR also traps.

A trap sets `mepc`, `mcause` and `mtval`, copies MIE to MPIE, clears MIE and jumps to `mtvec`.
`mret` undoes it. A misaligned load or store succeeds; a jump or taken branch to an address that
is not a multiple of four raises instruction-address-misaligned on the jump itself, and writes no
link register. There are no interrupts.

A program can read `cycle` and `instret` and compute its own CPI on the pipeline.

## 7. Pipeline

Five stages with the four classic latches. Each latch carries a sequence number given at fetch,
so one dynamic instruction can be followed across cycles.

| Switch | Values (default first) | Effect |
|---|---|---|
| Hazard handling | forwarding / stall only / off | Forwarding from EX/MEM and MEM/WB into EX. Stall only waits in ID until the producer reaches WB. Off leaves data hazards alone, so an instruction takes whatever the register file held, and gives wrong results on purpose; branches and system instructions still flush what is behind them. The run is checked in lockstep, and the trace names the first wrong value |
| Branch decision | EX / ID | Two squashed instructions, or one with a comparator and forwarding in ID. Decided in ID, a branch waits a cycle for an operand still in EX, and until a load ahead of it has finished MEM |
| Predictor | not taken / backward-taken static / 1-bit / 2-bit, the last two with a BTB of 16, 64 or 256 entries | The guess is made at fetch. The static rule takes backward conditional branches and every `jal`; a BTB entry is made when a branch is first taken, and is told the outcome at the end of the cycle that decides the branch |
| Multiply and divide | 1 cycle / N cycles, up to 64 | The instruction keeps EX for N cycles and holds ID and IF behind it, while bubbles go on to MEM: a stall that is not a hazard. Its operands are forwarded in its first cycle and kept |

A guess is wrong only when fetch went somewhere other than where the instruction really leads.
So a taken branch whose target is the very next instruction costs nothing under any predictor,
and the check is made for every instruction, not only for branches: after self-modifying code a
stale BTB entry can send fetch off after an instruction that is no longer a branch.

Rules, each with a test:

- Forward to operand A when `EX/MEM.regWrite`, `rd != 0` and `rd == rs1`; otherwise the same test
  on MEM/WB; otherwise the value read in ID. Operand B likewise. The register file writes before
  it reads.
- **A dependency counts only if the instruction uses that register** (`lui` has no `rs1`, an
  I-type has no `rs2`). Without this the diagram shows hazards that do not exist.
- Load-use: when `ID/EX` is a load whose `rd` is a used source of the instruction in ID, hold PC
  and IF/ID and send a bubble into EX.
- A redirect from EX outranks a stall in ID; a trap in MEM outranks both.
- **MEM is the commit point.** System instructions (CSR access, `ecall`, `ebreak`, `mret`,
  `fence.i`) take effect in MEM, then squash the three younger instructions and refetch. A system
  call can never run on a mispredicted path, and traps are precise. The instruction that trapped
  still travels to WB, so every instruction that commits is drawn through all five stages, and a
  run ends in the cycle its last instruction leaves WB.
- Every retired instruction produces the same commit record as the reference machine. The
  **lockstep checker** steps the reference once per commit and compares the two. The cycle counter
  is the one permitted difference: the pipeline's value is copied into the reference.

Each cycle emits a `CycleRecord`: for each stage the occupant and its state (normal, held, bubble,
squashed); the control signals and wire values the datapath view needs; and the events of that
cycle (`Forward`, `Stall`, `Flush`, `BranchResolved`, `RegWrite`, `MemRead`, `MemWrite`, `Commit`,
`Trap`). Nothing outside the engine reads the model; everything reads records.

For straight-line code of `N` instructions with forwarding, the run takes `N + 4` cycles plus one
for each load-use pair, plus `k - 1` for each multiply or divide when those take `k` cycles. A
test holds the pipeline to that on seeded random programs, and to the distance formula when
forwarding is off.

## 8. Visualizer

`Fetchline.Viz` is plain C# with no UI dependency, so it is unit-tested and shared by both front
ends: the staircase layout, the datapath's highlight sets, the explanations (message keys with
arguments, for English and Persian), the ASCII, JSON and Kanata writers, and the share codec.

### 8.1 The trace

The command line already shows the idea, and its output is what the golden tests compare:

```
$ fetchline trace examples/load-use.s
                          1    2    3    4    5    6    7    8
 0x00 lw   x4, 0(x2)      IF   ID   EX   MEM  WB
 0x04 add  x5, x4, x6          IF   ID   ID   EX   MEM  WB
 0x08 sub  x7, x5, x4               IF   IF   ID   EX   MEM  WB

 c3  stall    load-use: add (ID) needs x4; lw (EX) has it only after MEM
 c5  forward  MEM/WB -> EX.A   x4 from lw
 c6  forward  EX/MEM -> EX.A   x5 from add
 3 instructions, 8 cycles, CPI 2.67, 1 stall, 2 forwards
```

### 8.2 The playground

One page, state in the URL fragment.

Behind the page is a `Session` in `Fetchline.Viz` with no user interface in it: a source text,
the program it assembles to, and a run that can be stepped forwards and backwards. The registers,
memory and console it shows are folded up from the cycle records and from nothing else, so they
can be tested against the machine's own, and a step back is one record's changes taken out
again. It keeps the latest 20,000 cycles of records; a cycle older than that is reached by
running again from reset, which gives the same run because the machine has no other inputs.

- **Editor:** a textarea with a highlighted layer behind it. The highlighter is the assembler's
  own lexer and the squiggles are its own diagnostics. The gutter shows addresses and which stage
  each line is in. No editor library.
- **Staircase:** instructions down, cycles across, as in the textbooks. Held stages, bubbles and
  squashed instructions are drawn differently, and forwarding arrows run from producer to consumer.
  It is a grid of characters made by `StaircaseGrid`, which also makes the diagram of
  `fetchline trace`: without its arrows the two are the same text. A forward leaves its producer
  at the end of one cycle and enters its consumer at the start of the next, so its line runs down
  the last character of the earlier cycle's cells (`MEM─┐` above, `└EX` below). The pane shows as
  many of the latest cycles as fit across it, the cycle on screen as a lit column at the right,
  and on a narrow screen labels an instruction by its mnemonic alone.
- **Datapath:** an SVG of the five stages, latches, multiplexers, ALU, forwarding unit and hazard
  unit. The paths active in the current cycle light up, with values on hover. It is described as
  data (nodes, wires, ids), not drawn by hand in markup.
- **Hazard log:** each event as a sentence; clicking one jumps to its cycle. It lists what has
  been run, the cycles after the one on screen included, dimmer, so that after stepping back it is
  also the way forward again. With hazard handling off the first wrong value is one of its lines.
  Any stretch of a run can be explained on its own, because whatever an event is about was in
  the pipeline in the cycle of the event.
- **State:** registers with ABI names, memory, console, and counters (cycles, CPI, stalls and
  flushes by cause, forwards by path, prediction accuracy). The registers are named the way the
  program names them, and the one written in the cycle on screen is in inverse video. Memory is
  a hex dump, a word to a row in the order its bytes lie, of each section that is not code and
  of the stack from the stack pointer up, with the bytes just stored marked. Memory, the console
  and the counters share one pane with three names for a title. A counter for a cause that has
  not happened is left out.
- **Layout:** three, chosen by the size of the window. A phone has one column and the page
  scrolls. From 900 pixels wide the source sits beside the diagram and the log, with the state
  panes in a row below. From 1,200 by 600 the monitor fills the window and every pane scrolls
  inside itself: source, then diagram over log, then registers over the shared pane.
- **Controls:** run and pause, step, step back, reset, a speed, and a timeline to scrub. There
  is no assemble key: every keystroke in the editor assembles the text, and a program that
  assembles is a new run from reset. The controls are soft keys along the foot of the tube, each
  named with the function key that also works it: `F5` run or pause, `F10` step, `F9` back, `F8`
  reset, `F6` speed. A key is taken only when pressed alone, so `Ctrl+F5` still reloads the page.
  A run goes at one, four or sixteen cycles a second, or flat out in slices of several thousand
  cycles with a pause between them in which the page is drawn and the keys are heard. Step back
  and scrub move within the records the session holds, and replay from reset only for a cycle
  older than those. The status line says how the run stands: ready, at a cycle, running, paused
  at an `ebreak`, ended, exited with a code, stopped and why, and wrong since which cycle.
- **Timeline:** under the switches, the run so far as a line lit up to the cycle on screen,
  with a block for a handle. It is a range input in the tube's clothes, so a finger, a mouse and
  the arrow keys all move it; moving it shows that cycle on every pane.
- **Switches:** the what-if switches of section 7 are a row under the status line, each a name
  and its positions with the one chosen lit, named as the command line names them. Moving one
  builds the pipeline that way and starts the run again. What can be switched to is exactly
  what the tests run everything on: the 64 correct configurations, and the same with hazard
  handling off. The buffer's size is offered only while the predictor is one that has a buffer.
- **Examples:** a menu in the top edge of the source pane puts an example in the editor. The
  examples are the files under `examples/`, built into `Fetchline.Viz`, each offered with the
  first sentence of the comment it opens with, in the order a reader might take them: the four
  that each show one thing a pipeline does, then whole programs from the shortest to the longest.
- **Compare:** the current program under every configuration, as a table of cycles and CPI.
  `F7` puts it where the diagram is, and again brings the diagram back. It is the table
  `fetchline compare` prints, made by the same code a stretch at a time, so it fills in row by
  row and can be left half made; a configuration still running after 250,000 cycles is cut off
  and says so. The row of the configuration in use is lit, and choosing a row builds the
  pipeline that way.
- **Share and export:** a link holds the source, configuration and cycle (deflate, base64url, size
  capped and validated before use). The staircase exports as SVG or PNG, the trace as JSON or
  Kanata.
- Every string comes from a typed catalog, so a message missing from one language does not
  compile. Diagrams stay left to right in Persian.

### 8.3 The look: an old-school computer display

Asked for by the owner on 8 October 2026. The playground is drawn as the screen of an old
computer, not as a modern web app.

- **Frame:** the whole app sits inside a CRT monitor bezel with a slightly curved screen, a power
  light and a model plate carrying the project name. The case is the putty plastic of a 1980s
  terminal, lettered in a DIN-like face, so that it reads as an object and not as a dark page.
  It is plain HTML, on screen before the engine has loaded; the application is drawn into the
  tube.
- **Screen:** one phosphor colour on near-black, with a switch between green (the default), amber
  and white. Scanlines, a soft glow on text and a faint flicker are CSS only. The phosphor and
  plain switches are keys on the monitor's chin; they work without the engine and are remembered
  in the browser's local storage.
- **Type:** a monospace face everywhere on the screen, and a blinking block cursor in the editor.
  A bundled font must have an open licence (VT323 or IBM Plex Mono, both OFL) and needs the
  owner's yes to download; until then Cascadia Mono and Consolas are used.
- **Diagrams:** the staircase is a character grid, the same shape `fetchline trace` prints, so the
  browser and the terminal show one picture. Stalls, bubbles and squashed instructions are told
  apart by inverse video, brightness and blinking, not by a palette of colours. The datapath is
  drawn with thin single-colour lines, like a vector display, and active paths are the bright ones.
- **Panels:** registers, memory and the hazard log look like a machine monitor: hex dumps, a
  status line at the bottom, function-key labels (`F5 RUN`, `F10 STEP`) that are also real
  shortcuts.
- **Start-up:** the start-up screen is the loader. While the engine arrives the tube shows the
  machine's name, how much has arrived as a number and as a bar, and a blinking cursor; the
  application replaces it the moment the engine starts. Nothing is delayed for effect, so there
  is nothing to skip, and a later visit with the engine cached hardly sees it.
- **Rules that keep it usable:** a "plain display" switch turns every effect off;
  `prefers-reduced-motion` turns off flicker and blinking; text contrast stays at WCAG AA on each
  phosphor colour; nothing is conveyed by an effect alone. Persian text uses Sahel or Vazirmatn,
  because retro bitmap faces have no Persian glyphs, while code, registers and diagrams keep the
  monospace face.
- The look is one stylesheet of tokens (colours, glow, scanline strength), so it does not leak
  into the engine or `Fetchline.Viz`. A test reads it and holds it to the rules above: each
  phosphor's normal, dim and bright text at 4.5 to 1 or better against its unlit glass, every
  effect off under the plain switch, no animation for a reader who asks for less motion, and
  nothing fetched from another site.

## 9. Command line

`fetchline asm | dis | run | trace | compare | test`. `test <dir>` runs a folder of official ELF
files and exits non-zero on any failure, which is what CI calls. Every command writes to the
invocation's output, so the tests run commands in-process and compare what they print.

`trace` and `compare` take the what-if switches of section 7 as `--hazards forwarding|stall|off`,
`--branch ex|id`, `--predictor not-taken|backward-taken|1-bit|2-bit`, `--btb N` and `--muldiv N`.
`trace` draws the one configuration they describe. `compare` runs the program on every
combination of hazard handling, branch decision and predictor, except that a switch which is
given is kept fixed, and prints a row for each: its cycles, its CPI, the cycles lost to stalls,
the instructions squashed, and how many of its branches were guessed wrong. Every run has the
reference machine beside it, so a row that computed something else is marked wrong and the first
wrong value is named under the table. The names in a row are the names the switches take, so a
row can be typed back to `trace`. The table is made by `Comparison` in the engine and
`CompareTable` in `Fetchline.Viz`, which the playground's compare view will use as well.

`test --pipeline` takes the same switches and runs the official tests on a pipeline built that
way; a switch without `--pipeline` is refused, since it would change nothing.

## 10. Milestones

Each milestone is split into sections; a section is one commit, pushed when it is green. A box is
ticked in the commit that finishes the section.

**M0. Scaffold.** Done when `dotnet build` and `dotnet test` pass from a clean clone.

- [x] 0.1 The repository: licence, README stub, editor and git settings
- [x] 0.2 The solution, the engine project, the test project and the purity test
- [x] 0.3 The command-line project
- [x] 0.4 This specification and `CLAUDE.md`
- [x] 0.5 The CI workflow

**M1. Instruction table, decoder, encoder, disassembler.** Done when every instruction
round-trips over random fields and every match and mask equals the `riscv-opcodes` files.

- [x] 1.1 Bit fields and immediates
- [x] 1.2 The instruction table
- [x] 1.3 The decoder
- [x] 1.4 The encoder, and round trips on seeded random fields
- [x] 1.5 CSR names and the disassembler, canonical and alias forms
- [x] 1.6 The vectors workflow, and the cross-check against `riscv-opcodes`

**M2. Assembler, `Program`, ELF32; `fetchline asm` and `dis`.** Done when the examples assemble,
assembling a disassembled random word gives the word back, and every error has a line and column.

- [x] 2.1 Diagnostics and the lexer
- [x] 2.2 Expressions
- [x] 2.3 The statement parser: labels, directives, operands
- [x] 2.4 `Program`, sections, labels, constants and data directives
- [x] 2.5 Encoding real instructions, with relocation operators
- [x] 2.6 Pseudo-instructions
- [x] 2.7 The source map and the listing
- [x] 2.8 The ELF32 writer and reader
- [x] 2.9 `fetchline asm` and `dis`, and the first examples
- [x] 2.10 Disassemble-then-assemble round trips on random words

**M3. Reference machine and host calls; `fetchline run`.** Done when edge-case tests per
instruction pass, the example programs print what they should, and a benchmark is recorded in
`docs/CPU.md`.

- [x] 3.1 Memory: sparse pages and mapped regions
- [x] 3.2 Control signals and the `Exec` functions
- [x] 3.3 The reference machine and its commit records
- [x] 3.4 The host environment: system calls, pause, clean end, readable faults
- [x] 3.5 `fetchline run` and the example programs
- [x] 3.6 The benchmark project, and its first numbers in `docs/CPU.md`

**M4. CSRs, traps, `tohost`; the official tests.** Done when 42 `rv32ui` and 8 `rv32um` tests
pass, with the `rv32mi` tests that fit a machine-mode core, and CI runs them.

- [x] 4.1 The CSR file
- [x] 4.2 Traps, `mret` and the bare environment
- [x] 4.3 `tohost`, the conformance project and `fetchline test`
- [x] 4.4 The objdump cross-check of the decoder, disassembler and assembler
- [x] 4.5 CI runs the official tests

**M5. Pipeline with forwarding, load-use stall and EX branches; records; lockstep;
`fetchline trace`.** Done when the textbook sequences give the textbook diagrams, cycle counts
match the closed form on random straight-line code, and lockstep is clean on the examples and the
official tests.

- [x] 5.1 Latches and the two-phase kernel, on hazard-free code
- [x] 5.2 Forwarding
- [x] 5.3 The load-use stall
- [x] 5.4 Branches and jumps decided in EX
- [x] 5.5 System instructions and traps at the commit point
- [x] 5.6 Cycle records and events
- [x] 5.7 The lockstep checker
- [x] 5.8 The staircase layout, the ASCII writer and `fetchline trace`, with golden diagrams
- [x] 5.9 Closed-form cycle counts on seeded random programs; the official tests in lockstep

**M6. Hazard modes, ID branches, predictors, multi-cycle multiply and divide;
`fetchline compare`.** Done when the official tests and 2,000 random programs pass lockstep in
every correct configuration, and "off" reports its first wrong value.

- [x] 6.1 Stall-only hazard handling
- [x] 6.2 Hazard handling off, and the first-wrong-value report
- [x] 6.3 Branches decided in ID
- [x] 6.4 Static and dynamic predictors with a BTB
- [x] 6.5 Multi-cycle multiply and divide
- [x] 6.6 `fetchline compare`
- [x] 6.7 Lockstep across every configuration on random programs with branches

**M7. Web playground: editor, controls, staircase with arrows, hazard log, state panes.** Done
when, in the browser, you can type a load and its use, step, and read the stall explained; the
download size is measured and written down.

- [x] 7.1 The session: assemble, step, step back, run and reset, with no user interface in it
- [x] 7.2 The Blazor WebAssembly project and the display: bezel, phosphor, the plain switch
- [x] 7.3 The editor: the assembler's own lexer for highlighting, its diagnostics, the gutter
- [x] 7.4 The staircase as a character grid, with held and squashed marks and forwarding arrows
- [x] 7.5 The hazard log, and jumping to the cycle of a line
- [x] 7.6 The state panes: registers, memory, console and counters
- [x] 7.7 The controls, the function keys and the start-up sequence
- [x] 7.8 Checked in a real browser; the download size measured and written down

**M8. Datapath view, configuration, compare table, examples, share links, timeline.** Done when a
link opened in a fresh profile shows the same cycle and the same numbers.

- [x] 8.1 The configuration panel: the what-if switches
- [ ] 8.2 The datapath as data, and the paths that are active in a cycle
- [ ] 8.3 The datapath drawn, with values on hover
- [x] 8.4 The compare table
- [x] 8.5 The examples menu
- [ ] 8.6 The share codec, held to malformed and oversized input, and links that use it
- [x] 8.7 The timeline, and long runs in slices
- [ ] 8.8 Checked in a real browser: a link in a fresh profile shows the same cycle and numbers

**M9. README with real screenshots, `docs/CPU.md`, Pages workflow, exports, Persian.** Done when
the live site is checked by hand in both languages and a Kanata file opens in Konata.

- [ ] 9.1 Exports: the trace as JSON and as Kanata, the staircase as SVG and PNG
- [ ] 9.2 The Persian catalog, and a right-to-left page whose diagrams stay left to right
- [ ] 9.3 The README with real screenshots, and `docs/CPU.md` complete
- [ ] 9.4 The Pages workflow (switching Pages on is the owner's decision)
- [ ] 9.5 The live site checked by hand in both languages; a Kanata file opened in Konata

**M10+. One depth track** (a branch-prediction lab, a cache simulator, guided lessons or an
out-of-order core), with its own test and its own example.

M0 to M6 already make a complete, verified command-line tool; M7 and M8 make it something to
link to.

## 11. Verification

Automated, on every push:

- **Encodings:** round trips; the `riscv-opcodes` cross-check; and every instruction word in the
  official ELF files must disassemble to what GNU `objdump -M no-aliases` printed for it, and
  assemble back to the same word. That is several thousand instructions from a real toolchain,
  checked without one installed.
- **Semantics:** the official tests on the reference machine (`fetchline test`).
- **Pipeline timing:** golden diagrams for the textbook sequences; closed-form cycle counts on
  seeded random straight-line programs.
- **Pipeline correctness:** lockstep against the reference on the examples, the official tests
  and the random programs, under each configuration: 2,000 seeded random programs and the 66
  official tests on each of the 64 correct configurations the playground offers.
- **Determinism:** the hash of a record stream is stable, and the state after replaying to cycle
  `k` equals the state when first there.
- **Viz:** layout snapshots, explanation text in both languages, the Kanata golden file, and the
  share codec with malformed and oversized input.

By hand, in a real browser, at M7, M8 and M9: load an example, step to a stall and to a
misprediction, check the sentence, the arrows and the counters against `fetchline trace` for the
same program, open a share link in a fresh profile, and switch to Persian.

### 11.1 The test vectors

This machine has no RISC-V compiler. `.github/workflows/vectors.yml` builds the official tests on
a GitHub runner from pinned commits (`riscv-software-src/riscv-tests` at `bcffa2b3188b`,
`riscv/riscv-opcodes` at `5783cf3bea31`) and commits the ELF files, their objdump listings, the
six opcode files and a `PROVENANCE.md` that records the commits, the toolchain version and a hash
of every file. It runs only when started by hand. Both sources are BSD-3-Clause.

## 12. Deferred

Compressed instructions, floating point, atomics, interrupts and timers, supervisor mode and
virtual memory, RV64, superscalar and out-of-order execution, caches (unless chosen as the depth
track), a source-generated decoder, and `riscv-arch-test` (it needs RISCOF and a Sail reference).

Realism has no natural end. The rule that bounds it: a new CPU behaviour needs a test that shows
it and an example that teaches it.

## 13. Decisions that are the owner's

| Decision | Default in use | Settle before |
|---|---|---|
| Web UI: Blazor WebAssembly, or a C# engine behind `[JSExport]` with a React UI | Blazor | M7 |
| Phosphor colour and whether a specific machine is the model for the look | Green, no specific machine | M7 |
| Whether the repository is public | Private (created 8 October 2026) | M9, because Pages on a private repository needs a paid plan |
| Persian UI | Yes | M9 |
| The depth track | Not chosen | M10 |

## 14. Sources

- Ripes: <https://github.com/mortbopet/Ripes>
- QtRvSim manual: <https://comparch.edu.cvut.cz/qtrvsim/manual/>
- WebRISC-V: <https://arxiv.org/html/2504.03722v1>
- riscv-tests: <https://github.com/riscv-software-src/riscv-tests>, and its environment
  <https://github.com/riscv/riscv-test-env>
- riscv-opcodes: <https://github.com/riscv/riscv-opcodes>
- RISC-V ISA manual: <https://github.com/riscv/riscv-isa-manual>; assembly manual:
  <https://github.com/riscv-non-isa/riscv-asm-manual>
- Kanata log format: <https://github.com/shioyadan/Konata/blob/master/docs/kanata-log-format.md>
- Harris and Harris, *Digital Design and Computer Architecture, RISC-V Edition*, chapter 7;
  Patterson and Hennessy, *Computer Organization and Design, RISC-V Edition*, chapter 4
