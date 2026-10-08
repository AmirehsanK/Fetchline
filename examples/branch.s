# A branch that is not taken costs nothing. A branch that is taken costs the
# two instructions fetched behind it: they are thrown away, and fetch starts
# again at the target.

        li      x5, 1
        beq     x5, x0, skip        # not taken
        bne     x5, x0, skip        # taken
        addi    x6, x0, 1           # fetched, then squashed
        addi    x7, x0, 2           # fetched, then squashed
skip:
        addi    x8, x0, 3
