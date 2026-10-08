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
