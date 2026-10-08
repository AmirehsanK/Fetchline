# What a slow multiplier costs. The textbook pipeline gives every instruction
# one cycle in EX, but a real multiplier takes a few and a simple divider one
# for each bit of its answer. Draw this both ways:
#
#   fetchline trace examples/multiply.s
#   fetchline trace examples/multiply.s --muldiv 4
#
# With four cycles the mul stays in EX for four, the addi behind it waits in
# ID and the div in IF. Nothing is missing and nothing is on a wrong path:
# this is a stall that is not a hazard. When the mul is done the addi takes
# a2 from EX/MEM as it always would, and then the div holds everything up
# in its turn.

        li      a0, 6
        li      a1, 7
        mul     a2, a0, a1          # 42
        addi    a3, a2, 1           # 43
        div     a4, a2, a1          # 6
        sub     a5, a3, a4          # 37
