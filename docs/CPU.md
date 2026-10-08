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

The official `riscv-tests` come with M4, once CSRs and traps exist.

## 5. Measurements

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
