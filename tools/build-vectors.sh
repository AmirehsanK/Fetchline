#!/usr/bin/env bash
# Builds the official RISC-V test programs and collects the official opcode files, both from
# pinned commits, into <out-dir>. The development machine has no RISC-V toolchain, so this runs
# on a GitHub runner (see .github/workflows/vectors.yml and docs/SPEC.md 11.1).
#
# Usage: tools/build-vectors.sh <out-dir>
set -euo pipefail

RISCV_TESTS_REPO=https://github.com/riscv-software-src/riscv-tests
RISCV_TESTS_COMMIT=bcffa2b3188b040c611f90dc0b6e422f54775a09
RISCV_OPCODES_REPO=https://github.com/riscv/riscv-opcodes
RISCV_OPCODES_COMMIT=5783cf3bea312a48d28e3e2c8a2fcb55603e4bed
PREFIX=riscv64-unknown-elf-

out=${1:?usage: build-vectors.sh <out-dir>}
mkdir -p "$out"
out=$(cd "$out" && pwd)
work=$(mktemp -d)

rm -rf "$out/riscv-tests" "$out/riscv-opcodes"
mkdir -p "$out/riscv-tests/elf" "$out/riscv-tests/dump" "$out/riscv-opcodes"

# --- The test programs -------------------------------------------------------------------------

git clone --quiet "$RISCV_TESTS_REPO" "$work/riscv-tests"
git -C "$work/riscv-tests" checkout --quiet "$RISCV_TESTS_COMMIT"
git -C "$work/riscv-tests" submodule update --init --recursive --quiet
env_commit=$(git -C "$work/riscv-tests/env" rev-parse HEAD)

cd "$work/riscv-tests/isa"

# Only the "p" environment (physical memory, one hart) is built: the "v" environment compiles C
# against a libc this toolchain package does not ship, and it tests virtual memory, which the
# core does not have. The list of tests comes from the Makefile itself, not from this script.
tests=$(make --no-print-directory XLEN=32 RISCV_PREFIX="$PREFIX" \
  --eval='print-p: ; @echo $(rv32ui_p_tests) $(rv32um_p_tests) $(rv32mi_p_tests)' print-p)
# shellcheck disable=SC2086
make -j"$(nproc)" XLEN=32 RISCV_PREFIX="$PREFIX" $tests

for t in $tests; do
  cp "$t" "$out/riscv-tests/elf/$t"
  # Canonical mnemonics and ABI register names: what the disassembler here must reproduce.
  "${PREFIX}objdump" -d -M no-aliases "$t" > "$out/riscv-tests/dump/$t.dump"
done

cp "$work/riscv-tests/LICENSE" "$out/riscv-tests/LICENSE"
cp "$work/riscv-tests/env/LICENSE" "$out/riscv-tests/LICENSE.env"

# --- The opcode files --------------------------------------------------------------------------

git clone --quiet "$RISCV_OPCODES_REPO" "$work/riscv-opcodes"
git -C "$work/riscv-opcodes" checkout --quiet "$RISCV_OPCODES_COMMIT"

for f in rv_i rv32_i rv_m rv_zicsr rv_zifencei rv_system; do
  cp "$work/riscv-opcodes/extensions/$f" "$out/riscv-opcodes/$f"
done
# The CSR numbers and trap causes, to check the names the disassembler prints.
for f in csrs.csv csrs32.csv causes.csv LICENSE; do
  if [ -f "$work/riscv-opcodes/$f" ]; then
    cp "$work/riscv-opcodes/$f" "$out/riscv-opcodes/$f"
  fi
done

# --- Provenance --------------------------------------------------------------------------------

cd "$out"
{
  echo "# Provenance of the test vectors"
  echo
  echo "Everything under this directory was produced by \`tools/build-vectors.sh\` in the Vectors"
  echo "workflow. Do not edit it by hand; change the script and run the workflow again."
  echo
  echo "| | |"
  echo "|---|---|"
  echo "| riscv-tests | <$RISCV_TESTS_REPO> at \`$RISCV_TESTS_COMMIT\` |"
  echo "| riscv-test-env (its submodule) | \`$env_commit\` |"
  echo "| riscv-opcodes | <$RISCV_OPCODES_REPO> at \`$RISCV_OPCODES_COMMIT\` |"
  echo "| Compiler | \`$("${PREFIX}gcc" --version | head -n1)\` |"
  echo "| Binutils | \`$("${PREFIX}objdump" --version | head -n1)\` |"
  echo "| Build | \`make XLEN=32 RISCV_PREFIX=$PREFIX\` on the \`rv32ui\`, \`rv32um\` and \`rv32mi\` \`-p-\` targets of \`isa/Makefile\` |"
  echo "| Listings | \`${PREFIX}objdump -d -M no-aliases\` |"
  echo "| Runner | ${ImageOS:-local} ${ImageVersion:-} |"
  echo "| Workflow run | ${GITHUB_SERVER_URL:-}/${GITHUB_REPOSITORY:-}/actions/runs/${GITHUB_RUN_ID:-local} |"
  echo "| Built | $(date -u +%Y-%m-%d) |"
  echo
  echo "Both sources are BSD-3-Clause; their licence files are kept beside the files."
  echo
  echo "## Files (SHA-256)"
  echo
  echo '```'
  find riscv-tests riscv-opcodes -type f | LC_ALL=C sort | xargs sha256sum
  echo '```'
} > PROVENANCE.md

echo "Wrote $(find riscv-tests/elf -type f | wc -l) test programs to $out"
