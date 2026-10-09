# How it works, and how it was checked

`docs/SPEC.md` says what Fetchline is. This document says how each mechanism works and what
evidence there is that it is right. It is written alongside the engine, one mechanism at a time,
and a figure quoted here comes from a test or from the benchmark project.

## 1. The instruction table

`Isa/InstructionSet` has one row per instruction: 57 of them, for RV32I, M, Zicsr, Zifencei, `mret`
and `wfi`. A row holds the mnemonic, the encoding format, the match and the mask (a word is that
instruction when `(word & mask) == match`), the operand pattern its assembly takes, and its
control signals. Nothing else in the code knows an opcode: the decoder, the encoder, the assembler
and the disassembler are all driven by the table.

The control signals are the part both machines execute from. They say which of `rs1` and `rs2` an
instruction really reads, what the ALU's operands are and what it computes, whether memory is read
or written and how wide, how the program counter can change, and what is written back. `lui` does
not read `rs1` whatever bits sit in that field, and the table says so, which later keeps the
pipeline from reporting hazards that do not exist.

How it was checked:

- **Against the official definition.** `riscv/riscv-opcodes` is the set of files the RISC-V
  toolchains are generated from. A test parses the six files that cover this instruction set and
  requires every match and mask in the table to equal the official one, and the table to hold
  exactly the instructions of those files. Every CSR name is checked against `csrs.csv` the same
  way.
- **No two rows overlap.** For every pair of rows there is a bit that both fix and fix
  differently, so no word can be two instructions.
- **Round trips.** Every instruction survives encode-then-decode on 2,000 random field sets,
  half of them at or beside the ends of the immediate's range. In the other direction, two
  million random words are decoded and every legal one must encode back to its own bits.

The RV64 shift encodings (bit 25 set) decode as illegal, because RV32 reserves them; the official
`rv32mi-p-shamt` test checks exactly that.

## 2. The assembler

Two passes over a list of statements.

1. **The first pass** fixes the size of everything, and so the offset of every label within its
   section. It cannot know addresses yet, because the sections have not been placed, so a label's
   value in this pass is "this offset into that section". Arithmetic on such a value is known only
   when the section cancels out: `end - start` is a plain number when both are in one section,
   `start + 4` is still an offset, and `start + end` is unknown.
2. **Layout** gives each section its address: `.text` at 0, `.data` at `0x1000_0000`, then
   `.rodata` and `.bss`, each aligned to 16 bytes or to the strictest alignment asked for inside.
3. **The second pass** evaluates every expression with real addresses and writes the bytes.

`li` is the one instruction whose size depends on a value, and it is why the first pass tracks
what it can already compute. A constant known in the first pass becomes one `addi` when it fits
twelve signed bits, one `lui` when its low twelve bits are zero, and `lui` + `addi` otherwise.
Anything the first pass cannot compute (a symbol defined later, an address) is always two
instructions, so the size never changes between the passes.

Splitting a value for `lui` + `addi`, or a distance for `auipc` + `addi` in `la`, `call` and
`tail`, has one subtlety: the low twelve bits are added as a signed number, so when bit 11 is set
the upper part must be one more. `%hi` and `%lo` do the same split by hand.

Instruction forms are generated from the table: a row's operand pattern says what its assembly
looks like. The pseudo-instructions are listed by hand, each as the real instructions it becomes.

Mistakes are collected, never thrown, and reported in the order of the source with a line, a
column and a caret. A name where a register belongs gets a suggestion (`a8` suggests `a7`), as do
misspelled mnemonics, directives, symbols and CSRs.

How it was checked:

- **The disassembler writes what the assembler reads.** 60,000 random words, at random addresses,
  are disassembled in canonical form and in alias form, and each text must assemble at that
  address back to the same word. A second test walks every alias once.
- **Every pseudo-instruction against its official definition.** `riscv-opcodes` also defines the
  pseudo-instructions as a real instruction with more bits fixed; an instance of each is assembled
  and held to that match and mask.
- **`li` by its effect.** 5,000 values are loaded with `li`, and a test adds up what the resulting
  `lui` and `addi` do, in 32 bits, and requires the value asked for.
- **Robustness.** The lexer is run on 3,000 random strings and the parser on 4,000 programs built
  from the pieces of real ones; neither may fail, and every diagnostic must lie inside the source.

## 3. ELF files

`Elf/ElfFile` writes a program as a 32-bit little-endian RISC-V executable and reads one back:
the loadable segments, the symbol table and the entry point. Segments are written at file offsets
congruent to their addresses modulo the page size, which a loader that maps whole pages requires.

How it was checked: a program survives being written and read back with every segment, symbol and
flag intact; the 66 official test programs, built by GCC on a GitHub runner, are read with their
symbols; and 6,000 damaged copies of two files are read without the reader ever failing. It
returns a program or a reason.

## 4. The reference machine

One whole instruction per step. A step fetches the word, decodes it (through a cache keyed by
address that also remembers the word, so code that rewrites itself still runs correctly), reads
`rs1` and `rs2`, and calls the `Exec` functions for the ALU result, the branch decision and the
target. Then it calls `Hart.Complete`.

**`Hart.Complete` is the commit point.** It decides everything an instruction does to the world
apart from writing its register: memory is read or written, a system call runs, a fault is
detected. It returns a commit record: the register written and its value, the store made, where
control goes next, and whether the machine stopped. The reference machine then writes the register
and follows the next address. The pipeline will call the same function from its memory stage with
the values its own plumbing delivered, and write the register a stage later. Two machines are
equal when their records are equal, so a forwarding mistake in the pipeline will show up as a
record that differs, at the instruction where it happened.

A jump to an address that is not a multiple of four faults on the jump itself and writes no link
register, as the privileged specification requires of a core without compressed instructions.

**The host environment** is what a program run by `fetchline run` or the playground lives in. A
memory map gives it its own segments with their own rights, a heap of 256 MB from the start of the
data, and a megabyte of stack. Anything else stops the program with a sentence that names the
address and the `pc`: a store to its own code, a load from nowhere, a jump into data, recursion
that runs out of stack. `ecall` is one of six system calls, `ebreak` pauses, and running off the
end of the code is a clean stop. `ra` starts at that end address, so a program whose entry
function finishes with `ret` ends cleanly too.

How it was checked:

- **`Exec` at its edges.** One row per edge case from the ISA manual: shifts by more than 31,
  signed against unsigned comparison, division by zero, the one signed overflow. A test fails if
  an operation has no row.
- **Multiply and divide against arbitrary precision.** All eight M operations are compared with
  `BigInteger` arithmetic on 100,000 operand pairs.
- **Memory against a flat array.** 200,000 random reads and writes of each width, across page
  boundaries, must agree with a plain array.
- **Programs.** Each example in `examples/` has its expected output in a test, and a new example
  fails that test until it says what it prints.

## 5. CSRs and traps

The hart has machine mode and nothing else. Its CSR file holds `mstatus` (the interrupt enable and
its saved copy; the previous-privilege field can only read as machine), `misa`, `mie`, `mip`,
`mtvec` (direct mode), `mscratch`, `mepc`, `mcause`, `mtval`, the identification registers, and
two 64-bit counters under their machine and unprivileged names.

**A register that is not there does not exist**, and touching it is an illegal instruction. This
is load-bearing. The official tests' start-up code writes `satp`, `pmpaddr0`, `medeleg` and
others, each time after pointing `mtvec` at the next label, precisely so that a core without the
feature traps and carries on. A core that quietly accepted those writes would be claiming
features it lacks.

On bare metal an exception is delivered: `mepc`, `mcause` and `mtval` are written, the interrupt
enable is saved and cleared, and control goes to `mtvec`. `mret` undoes it. `ecall` and `ebreak`
trap. In the host environment there is no handler to go to, so the same exception stops the
program with a sentence instead.

Three details the tests insist on:

- A CSR set or clear instruction whose operand field is zero writes nothing. That is what makes
  `csrr a0, mhartid` legal although `mhartid` is read-only.
- An instruction that writes `minstret` sets the value the next instruction reads, so it does not
  also count itself. A trapping instruction does not complete and is not counted.
- A misaligned load or store succeeds (`rv32ui-p-ma_data` requires it), while a jump to an address
  that is not a multiple of four traps on the jump.

How it was checked:

- **The official tests.** `riscv-software-src/riscv-tests`, built with GCC 13.2 on a GitHub
  runner from a pinned commit (`tests/vectors/PROVENANCE.md`). On the reference machine all 42
  `rv32ui` tests, all 8 `rv32um` tests and 14 of the 16 `rv32mi` tests pass. The two that do not
  each need something this core does not have: `rv32mi-p-breakpoint` needs the debug trigger
  registers, and `rv32mi-p-pmpaddr` needs physical memory protection. They are listed with those
  reasons in `tests/conformance-exclusions.txt`. An excluded test is still run, and if it ever
  passes that is reported as a failure, so the list cannot go stale.
- **GNU objdump's reading of the same programs.** The listings objdump printed for the test
  programs hold 19,217 instructions. For each word this core must name the same instruction with
  the same operands, and its own text for the word must assemble back to it. Every instruction in
  the table appears in those listings at least once.
- **Traps by hand.** Small bare-metal programs check each cause, what lands in `mepc` and `mtval`,
  that the instruction that trapped wrote nothing, and that code which rewrites itself is decoded
  again.

## 6. The pipeline

Five stages, IF, ID, EX, MEM and WB, with the four latches between them. Each latch carries the
sequence number its instruction was given at fetch, so one dynamic instruction can be followed
from cycle to cycle; an instruction that is fetched again after a flush gets a new number, and a
new row in the diagram.

**The kernel is two-phase.** In a cycle, every stage first works out its result from the latches
as they stand, in the order WB, MEM, EX, ID, IF, and then all the latches take their new values
at once. The backwards order is what lets a late stage act on an early one within the cycle, as a
wire would: WB writes the register file before ID reads it, a redirect computed in EX reaches IF,
a flush decided in MEM reaches everything behind it. Nothing else crosses between stages, so
there are no accidents of evaluation order.

**Forwarding.** An operand of the instruction in EX is the newest value there is: the result in
EX/MEM if the instruction one ahead writes that register, else the one in MEM/WB, else what ID
read. An operand the instruction does not really use is left alone, so `addi x6, x7, 4` after a
write to `x4` forwards nothing. Forwarding costs no cycles.

**The load-use stall.** A loaded value does not exist until the end of MEM, one cycle too late for
the next instruction to take it into EX. When the instruction in EX is a load whose register the
instruction in ID really uses, ID keeps its instruction, IF keeps the one behind, and a bubble
goes into EX. The instruction waiting in IF is not fetched again; it keeps its number.

**Branches.** Fetch carries on in a straight line. A branch or jump that is taken in EX throws
away the two instructions fetched behind it. A redirect outranks a stall in ID, and if fetch had
run off the end of the code on the wrong path, that is forgotten.

**The commit point.** `Hart.Complete` is called from MEM, with the operands the pipeline's own
plumbing delivered. A system instruction, a trap or a stop takes effect there, and the three
instructions behind it are squashed and fetched again from wherever it says control goes. So a
system call cannot run on a wrong path, an instruction that traps has changed nothing, and the
instruction after a CSR read sees what was read. An instruction that stops the machine still
travels to WB, and the run ends in the cycle it leaves; running off the end of the code ends the
run in the cycle the last instruction leaves WB.

**Records and events.** A cycle produces a record: what is in each stage and whether it is held or
squashed, the commit record of the instruction leaving WB, and events. An event is said by the
logic that did the thing: the forwarding unit says what it forwarded, from which latch, to which
operand, from which instruction; the hazard unit says what it stalled and why. Nothing is worked
out afterwards from what the stages held. The diagram, the counters and the sentences of the log
are all made from the records, so they cannot disagree with the engine or with each other.

How it was checked:

- **Lockstep.** The pipeline runs with the reference machine beside it; the reference is stepped
  once for each instruction the pipeline commits and the two commit records must be equal. Both
  come out of the same `Hart.Complete`, so a difference can only be the pipeline's plumbing. The
  cycle counter is the one permitted difference (the pipeline's reading is handed across); the
  instruction counter must agree exactly. Lockstep is clean on every example, on 300 random
  programs of dependent arithmetic, 300 with loads and stores, 400 with forward branches and
  jumps, and on all 66 official tests, including the two excluded ones, which fail the same way
  on both machines.
- **The closed form.** For straight-line code the length of a run is known without running it:
  N + 4 cycles, plus one for each load whose value the very next instruction uses. 1,000 random
  programs are held to it, with the pairs counted from the program text, not from the engine.
- **The textbook diagrams.** The load-use sequence, the forwarding sequence and a taken branch
  give the diagrams the textbooks draw; they are pinned as golden files under `tests/golden`,
  and the load-use one is the diagram in the specification, character for character.
- **The records are complete.** The playground's session never looks inside the machine: it
  folds its registers, memory and console out of the records alone. A second machine runs beside
  it and is looked inside, and the two agree after every cycle, on the examples and on 120 random
  programs spread over every combination of hazard handling, branch decision and predictor,
  "off" included. Stepping back takes a record's changes out again; every cycle of a run shows
  the same thing whether it is reached forwards, backwards or by jumping straight to it.
- **The wires.** Each record also says what was on the datapath's wires that cycle: where the
  program counter's next value came from, what ID read, the operands in EX after forwarding, the
  ALU's inputs and result, and what MEM was handed. The datapath view shows these and works
  nothing out for itself. They are held to the rest of the record on 60 random programs built
  all 64 correct ways: a value an event says was forwarded is the value EX used, what is fetched
  next is fetched from where the counter was sent, what leaves EX is what MEM has a cycle later,
  and an ALU result that is written to a register is the value the commit record gives two
  cycles on. The drawing itself is data too (parts, wires and their corners), and a test holds
  it to the rules of a drawing for every way the pipeline can be built: each wire begins and
  ends on the parts it names, runs level or upright, and goes round every part in its way. A
  last test runs the examples and checks that no wire and no part is there for nothing: each is
  lit by something.
- **Determinism.** A run can be hashed field by field with FNV-1a. The same program gives the
  same number every time, and the number for `examples/load-use.s` is pinned. Replaying a program
  to a cycle gives the state, the events and the commit that were there the first time, which is
  what makes stepping back a replay.
- **Timing over the whole suite.** The 66 official tests complete 19,982 instructions in 26,575
  cycles on the pipeline, a CPI of 1.33. A test program cannot check its own timing, so those two
  numbers are pinned by a test.

## 7. The what-if switches

The pipeline of section 6 is one way to build it. `PipelineConfig` holds the others, and
`fetchline trace` takes them as `--hazards`, `--branch`, `--predictor`, `--btb` and `--muldiv`. A
switch changes how long a program takes and never what it computes: built any way but one, the
pipeline commits the same instructions with the same results as the reference machine, and
lockstep checks that it does. The one exception is there on purpose.

**Stalling only.** No forwarding paths at all. The only way to a value is the register file, so
an instruction waits in ID until the one producing its value is in WB, where a write is read in
the same cycle. A consumer straight behind its producer waits two cycles, one that is two behind
waits one, and three behind is far enough. A load is waited for like anything else, and when two
instructions ahead write the same register it is the nearer one that is waited for.

**Off.** Nothing watches the data hazards: an instruction takes whatever the register file held
when it left ID. Branches and system instructions still flush what is behind them, so the program
still runs, and computes the wrong answer. The reference machine runs beside it, the first
instruction on which the two differ is reported with both values, and after that nothing more is
compared. `examples/sum.s` prints 65 where it should print 55, and both errors can be found by
hand: the first `add` reads the counter before the `li` ahead of it has written it, and the
branch at the foot of the loop is always one behind, so the loop goes round an eleventh time.

**Branches decided in ID.** A comparator in ID decides the branch a stage early, so a taken one
throws away one instruction and not two. The price is that the operands are needed a stage early
as well. A result being computed in EX is waited for, one cycle, and then taken from EX/MEM into
ID; a load is waited for until it has finished MEM. So the earlier decision is not always the
faster one: a loop of `addi`, `addi`, `bnez` that goes round three times takes 18 cycles decided
in EX and 19 decided in ID, because every `bnez` waits a cycle for the `addi` ahead of it.

**Predictors.** Where to fetch next is guessed at fetch, from the address and the word alone.
Not-taken always goes straight on. The static rule takes a conditional branch that goes
backwards and every `jal`, whose target can be read off the word; an indirect jump cannot be
read off anything. The one-bit and two-bit predictors keep a branch target buffer, direct-mapped
and tagged: an entry is made the first time a branch is taken, a one-bit entry expects the
branch to do what it did last time, and a two-bit entry is a saturating counter that takes two
wrong guesses in a row to change its mind. The buffer is told how a branch came out at the end
of the cycle that decided it, like any other state.

A guess is wrong only when fetch went somewhere other than where the instruction really leads.
That one rule covers every case: a taken branch whose target is the next instruction costs
nothing, a branch guessed taken that falls through is put right the other way, and the question
is asked of every instruction, so a stale entry that sends fetch off after something that is no
longer a branch is corrected too.

**Multiply and divide over several cycles.** `--muldiv N` gives a multiply or a divide N cycles
in EX, as a real multiplier takes a few and a divider that produces a bit at a time takes thirty
or so. While it works it keeps EX, the two instructions behind it keep ID and IF, and bubbles
follow the instructions ahead of it through MEM and WB. Nothing is missing and nothing is on a
wrong path: it is the one stall here that no hazard causes. The operands are forwarded once, in
the first cycle, and kept, as a multiplier latches its inputs when it starts; by the last cycle
the instructions they came from have left the pipeline. An instruction in MEM that flushes what
is behind it can only catch a multiply in its first cycle, since after that there are bubbles
ahead of it, and then the multiply starts again from the beginning when it is fetched again.

**Comparing them.** `fetchline compare` runs one program on every combination of hazard
handling, branch decision and predictor, each with the reference machine beside it, and prints a
row for each: cycles, CPI, cycles lost to stalls, instructions squashed, and branches guessed
wrong. For `examples/sum.s`, forty instructions with a loop that goes round ten times, the table
can be worked out by hand, and a test holds it to that. Decided in EX with forwarding the run
takes 68 cycles guessing not-taken, 52 with the static rule and 54 with either dynamic
predictor; decided in ID it takes 69, 61 and 62, because each of the ten branches waits a cycle
for the `addi` ahead of it. Stalling only, the waits come to 21 cycles whichever stage decides
the branch, so there the earlier decision is the faster one: 80 cycles against 89 guessing
not-taken. A configuration that computed something else is marked wrong, and "off" is marked
only when it is: a program with no close dependencies is right however it is run.

How they were checked:

- **Every correct configuration at once.** The playground offers 64 correct ways to build the
  pipeline: forwarding or stalling only, branches in EX or in ID, the four predictors with the
  two that keep a table at 16, 64 and 256 entries, and a multiplier of one cycle or three. 2,000
  random programs run in lockstep on every one of them, 128,000 runs, and so do the 66 official
  tests. The programs are made to end whatever their data turns out to be, and are made of
  everything the hazard logic has to get right: arithmetic on what was just computed, loads and
  stores at computed addresses, multiplies and divides, forward branches on data, counted loops
  two deep with early exits, calls direct and through a register, CSR accesses and system
  calls. A separate test counts what they exercise, so that the sweep cannot pass by not trying.
  300 of them run again outside what the playground offers: buffers of 1, 2, 4 and 65,536
  entries, and multipliers of 2, 5, 7 and 64 cycles.
- **The sweep can fail.** When it was written, two faults were put into the pipeline on purpose
  and taken out again: a slow multiply that did not keep its forwarded operands, and a branch in
  ID that did not wait for a load still in MEM. Each was reported with the number of the program,
  the switches that showed it and the first wrong instruction.
- **Off never breaks the machine.** The same programs run with hazard handling off, 2,000 runs.
  Nearly all go wrong, and then do whatever wrong values make them do: many end in a fault, a
  few never end and are cut off, and the rest reach an end of their own. The simulator itself
  never fails, and every report is true: the two records shown really differ, nothing was
  compared after them, and a run with no report ended with the registers the reference has.
- **Lockstep on the examples.** Every example runs in lockstep stalling only, with branches
  decided in ID, under each of the four predictors with both branch decisions, and with a slow
  multiplier in five combinations of the other switches. 400 random programs of branches,
  jumps, loads and stores run in lockstep with branches decided in ID, with forwarding and
  again stalling only.
- **Stalling only has a closed form too.** If `d(i)` is the cycle instruction `i` leaves ID and
  `c(i)` the cycles it spends in EX, then `d(i)` is the larger of `d(i-1) + c(i-1)` and, for each
  producer `p` of a value it reads, `d(p) + c(p) + 2`; the run ends `c + 2` cycles after the last
  instruction leaves ID. 500 random programs are held to that with a one-cycle multiplier and
  500 more with multiplies and divides of one to six cycles.
- **Off goes wrong, and says so truthfully.** Of 300 random programs of dependent arithmetic
  more than 250 compute something wrong, and in every one the two records reported really
  differ and every instruction before them really matched.
- **The comparator's waits, case by case.** Ten placements of a branch behind the instruction
  that computes its operand (in EX, in MEM, in WB, an ALU result or a load, one operand or both)
  give the stalls and forwards worked out by hand for each.
- **The predictors against a second statement of their rules.** Each rule is written again in
  the tests as a dictionary by address, fed by the reference machine one branch at a time. On 150
  random programs of loops, forward branches and jumps, the wrong guesses it counts are the
  wrong guesses the pipeline made, under all four predictors and both branch decisions.
- **Loops, by hand.** A loop that goes round five times is guessed wrong 4 times by not-taken,
  once by backward-taken and twice by either dynamic predictor, and takes 27 + 4 cycles plus two
  for each wrong guess. In a nest of two loops one bit is wrong 8 times and two bits 6. Two
  branches 64 bytes apart share an entry of a 16-entry buffer and push each other out: 26 wrong
  guesses, against 16 with 64 entries.
- **A slow multiplier adds exactly its own cycles.** With forwarding, straight-line code takes
  N + 4 cycles, plus one for each load-use pair, plus k - 1 for each multiply or divide of k
  cycles; 600 random programs are held to that. The same holds for every example with branches
  decided in EX: the run is longer by k - 1 cycles for each multiply and divide the reference
  machine completes, for k of 2, 5 and 32. Built the other ways the extra cycles are a ceiling,
  not a sum, because a wait behind the multiplier can be the same cycles as a wait for a value.
- **Timing over the official tests again.** With the rule about wrong guesses in place the 66
  tests take 26,575 cycles where they took 26,581: three jumps in the suite go to the very next
  instruction, which is where fetch was going anyway. Lockstep cannot see a pipeline that is
  right but slower than it was, so the total is pinned with each switch moved on its own:

  | Built | Cycles for the 66 tests |
  |---|---|
  | forwarding, EX, not-taken (the textbook pipeline) | 26,575 |
  | stalling only | 39,740 |
  | branches decided in ID | 28,229 |
  | backward-taken | 26,209 |
  | one-bit, and two-bit | 27,183 |
  | a multiplier of three cycles | 27,043 |

  The predictors that learn are slower here than guessing not-taken, and that is right. The
  tests are full of loops that go round exactly twice: not-taken is wrong once, on the way
  back, while a predictor that has just seen the branch taken expects it taken again and is
  wrong both times. The slow multiplier adds exactly two cycles for each of the 234 multiplies
  and divides the suite completes, a count taken on the reference machine.
- **Off against the official tests.** With hazard handling off, `fetchline test --pipeline`
  fails all 64 of them, each at a named first wrong value: the tests are not so gentle that a
  pipeline with no hazard logic gets through one.

## 8. Measurements

Taken with `dotnet run -c Release --project bench/Fetchline.Benchmarks -- --filter '*'` on
8 October 2026: BenchmarkDotNet 0.15.8, default job, .NET 10.0.11, Intel Core i7-9700K at 3.6 GHz,
Windows 11.

| What | Result |
|---|---|
| Reference machine, `examples/primes.s` (149,814 instructions) | 3.766 ms a run: 25 ns an instruction, 40 million instructions a second |
| Decoding one word | 9.5 ns |
| Assembling `examples/bubble-sort.s` (46 lines) | 22.5 µs, 64 KB allocated |

The interpreter is not built for speed: every step produces a full commit record, because being
comparable with the pipeline matters more here than being fast. Two things were worth doing
anyway. Memory keeps a small table of recently used pages, hashed so that code at `0x0000_0000`
and data at `0x1000_0000` do not evict each other; and the record of a step is copied into the
machine only when it is a stop. Together they took a run from 4.64 ms to 3.77 ms.

### 8.1 What the playground costs to download

Taken with `pwsh tools/site-size.ps1` on 9 October 2026, which publishes the playground in
Release (trimmed, .NET 10.0.11, without the `wasm-tools` workload) and adds up what was
published. Publishing writes a gzip and a Brotli copy beside every file, so the three columns
are the same 45 files three ways, and the first visit downloads all of them.

| What | As they are | With gzip | With Brotli |
|---|---|---|---|
| Everything a first visit downloads | 6,815,446 bytes | 2,705,130 | 2,219,595 |
| The .NET runtime, `dotnet.native.wasm` | 3,002,094 | 1,207,892 | 976,842 |
| The core library, `System.Private.CoreLib` | 1,482,005 | 567,151 | 458,277 |
| Fetchline itself: the engine, `Fetchline.Viz`, the page, its stylesheet and scripts | 466,505 | 194,015 | 158,420 |

So the download is 2.2 MB from a server that sends Brotli and 2.7 MB from one that sends gzip,
and one part in fourteen of it is Fetchline; the rest is the runtime it runs on. The engine was
kept free of package references partly for this: `Fetchline.Core` is 201 KB as it is and 68 KB
compressed. Share links brought in the one library that was not there before them,
`System.IO.Compression`, which is 27 KB as it is and 10 KB compressed. The runtime's share
could be cut by relinking it, which needs the `wasm-tools` workload; that is not installed here
and has not been tried.

CI publishes the playground on every push, prints the same three totals and fails if the Brotli
total passes 3 MB. The published files were also loaded from a plain static server
(`tools/serve.cs`), not the development one: a load and its use were typed into the editor,
stepped with F10 three times, and the log read `load-use: addi (ID) needs t0; lw (EX) has it
only after MEM`.

A share link was checked the same way. On the development server the bubble sort was loaded, the
switches set to stalling, branches in ID, a 1-bit predictor and a three-cycle multiplier, and
the run stepped to cycle 157; `F2` put the link, 692 characters of it, in the address. Opened
from the published files on another port, which to a browser is another site with nothing
stored for it, the page came up at cycle 157 with the same switches, the same source, the same
registers and log, and the same counters: 91 instructions, 51 stalls, 12 flushes, 26 branches of
which 12 guessed wrong. Pasting a link over the address of a page that is already open loads it
too, and one with two characters changed is refused with `THE LINK COULD NOT BE READ: IT IS
DAMAGED` while what was on screen stays.

The same build is live at <https://amirehsank.github.io/Fetchline/>, published by the Pages
workflow on 9 October 2026 and checked there in both languages: the load-use example stepped to
cycle 6 reads the same three lines of log in English and in Persian, the datapath and the
comparison table come up, the diagram stays left to right in a page that runs from the right,
and nothing it asks for fails to load. Pages sends the files with gzip, and a first visit
transferred 2,659,284 bytes.

## 9. What is drawn, shared and saved

Nothing in the visualizer looks inside the machine. Each of these is made from the cycle
records, in `Fetchline.Viz`, where it is tested without a browser; the page only draws it.

- **The diagram.** One piece of code places every character of the staircase, for the terminal
  and for the page, and the golden files under `tests/golden` are its output. The page adds the
  lines of the forwards; a picture of the diagram is the same grid placed cell by cell, so its
  columns line up whatever face draws it.
- **The datapath.** Section 6 says how the wires are held to the records. What is lit is decided
  by the instruction's control signals and the cycle's events, which are the engine's own.
- **Share links.** A link is `1.` and then the source, the switches and the cycle, deflated and
  written in the alphabet that needs no escaping in an address. It is untrusted input, so
  reading one is the careful half: the text is capped at 48,000 characters before anything is
  decoded, what it inflates to is capped at 128,000 bytes while it inflates, numbers are read
  strictly, a setting the encoder never writes is refused, and so is a cycle past 250,000. A
  link carries a check of its source, so one that lost its end in a chat window is refused and
  does not quietly run some shorter program. The tests take a good link and damage it 6,000
  ways, feed the reader random text, forged headers and a small link that inflates to far too
  much; every one is refused with a reason, and none throws.
- **Exports.** The JSON is the record stream written out, with each instruction as text beside
  its word; a test reads it back and finds the stall, the forward and the end of the run where
  the records have them. The Kanata log follows Konata's own description of the format, and a
  test holds it to that description on the examples built three ways: ids count up from zero in
  the order instructions appear, a stage is ended before the next begins, every instruction is
  retired or flushed exactly once, and the two counts are the run's instructions and its
  squashed instructions. The log of the load-use hazard is kept as a golden file. Two logs were then
  opened in Konata 1.2.0 itself, which since 1.0 is a page in a browser: the load-use hazard came
  up as 3 instructions over 8 cycles with the held ID stage as a box two cycles long and an
  arrow into each EX that took a forwarded value, and the bubble sort as 371 instructions over
  397 cycles. That is where the second lane was dropped: a stall drawn in it covered the name
  of the stage that was waiting.
- **Two languages.** A message is a method of an interface, so a language that lacks one does
  not compile. A test goes through every message of both catalogs, more than 150 with each
  value of each enumeration, and checks that it says something and that every name or number
  it was given is still in what it says. Another checks that the Persian catalog is in Persian
  wherever it is a sentence, and that the comparison table, which is text in columns, keeps to
  characters one cell wide in Persian too.

## 10. Caches

The depth track. A cache here is a model of time and of nothing else: it holds the tags of the
blocks it would have, and no data. The data is in the machine's memory, where it always was, so
a cache cannot make a program compute something else. That is the whole design, and it is why
the checks below can be as strong as they are.

- **The cache.** Sets, ways and bytes in a block are each a power of two (the ways need not be);
  an address is a tag, a set and an offset. A block that is not there goes into an empty way
  if the set has one, the lowest numbered first, and otherwise over the way used longest ago.
  "Longest ago" is counted in accesses to that cache, a clock of its own, so what a cache does
  depends on the order of its accesses and on nothing else.
- **A miss in MEM** holds the instruction there for the penalty, sends bubbles on to WB, and
  holds everything behind it. MEM is the commit point, so the instruction takes effect once,
  when the wait is over. There is one thing a held instruction must do while it waits: one in
  EX takes its forwarded operands in the first cycle of the wait. The instruction in WB that an
  operand may come from is there for that cycle only; by the time EX is let go it has left the
  pipeline, and what ID read is stale.
- **A miss in IF** holds only fetch. The instruction is in IF for the penalty and bubbles go on
  to ID; what is ahead of it carries on. If the pipeline is redirected while it waits, it is
  squashed like anything else in IF and its block stays in the cache, as it would. The guess
  about where fetch goes next is made when the word arrives, not when it was asked for.
- **A store** that misses brings its block in like a load, and pays the same. Nothing is
  written back later, because there is nothing in the cache to write.
- **Events.** Every access is an event with its set, its way, the block and the block put out;
  every cycle of waiting is a stall whose cause is the miss. The log, the counters and the
  playground's view of what a cache holds are made from those.

How it was checked:

- **The cache against a second statement of its rules.** The rules again as a list for each set
  in the order of use: a block in the list is a hit and goes to the end; one that is not goes
  to the end too, and if the list is then longer than the ways, what is at the front is put
  out. Seven shapes, 4,000 random accesses each, and the cache agrees on every hit, every set
  and every block put out. By hand: a direct-mapped cache where two addresses fight over a
  set; two ways where the one used longest ago goes; one block more than the ways, taken in
  turn, misses every time, and one fewer never misses after the first round.
- **Rows by hand.** A load that misses with a penalty of three is in MEM four cycles, the
  instruction behind it in EX four, and the next load of the same block hits. An instruction
  that misses in IF with a penalty of two is in IF three cycles while the one ahead of it is
  never held. A store takes effect in the last cycle of its wait and not before.
- **The operand that has to be taken in time.** `li t0, 5`, a store that misses, `addi t1, t0, 1`:
  the forward from MEM/WB happens in cycle 5, the first of the wait, and the answer is 6. With
  that one line of the engine switched off, this test fails, and so does the sweep below.
- **Lockstep, every shape, every correct configuration.** 300 random programs, each with one
  of nine pairs of caches (one word that nearly always misses, both caches small with different
  penalties, both big enough to miss once), on all 64 correct configurations, in lockstep with
  the reference machine: no difference, and more than 100,000 misses on the way. Then every
  instruction cache the playground offers with every data cache it offers, twelve pairs, on 40
  programs and all 64 configurations: 30,720 runs, no difference. And the official tests, on
  the command line, with caches small enough to miss all the time: 64 pass.
- **What a miss costs, as a formula.** A miss in MEM stops everything, so it costs exactly its
  penalty: with only a data cache, the run takes the cycles it takes without one plus the
  penalty for each miss, and the sweep holds every run to that. A miss in IF costs at most its
  penalty, since a wait that coincides with a stall is served at the same time, and the sweep
  holds every run to that as well. (A program that reads the cycle counter is left out: it can
  do something else when it reads another number.)
- **Off never breaks.** 120 random programs with hazard handling off and caches on run to
  whatever end they come to without the engine throwing.
- **The view is the machine's.** The playground folds what each cache holds out of the events,
  and steps it back. A second machine runs beside it and is looked inside: on two examples and
  24 random programs with three pairs of caches, the two agree after every cycle, and again at
  60 cycles jumped to in any order.
- **The example.** `examples/cache-rows.s` and `examples/cache-columns.s` add up the same table
  of 8 rows of 8 words and print 2080. With no cache each takes 749 cycles. With a direct-mapped
  data cache of four 16-byte blocks and a penalty of 10 (`--dcache 4x1x16:10`), going along the
  rows misses 16 times in 64 loads, once for each block of four words, and takes 909 cycles;
  going down the columns misses all 64 times, each block landing on the one brought in two
  loads before, and takes 1,389. Two ways do not help, because a column is eight blocks that
  all want the same set. A cache the whole table fits in (`16x1x16:10`) makes the order not
  matter again: 909 both ways. These figures are pinned by a test.
