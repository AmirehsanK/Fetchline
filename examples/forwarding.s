# The textbook forwarding sequence. Every instruction after the first reads
# x2, and each gets it a different way:
#
#   and   one behind: x2 is in EX/MEM, and is forwarded from there
#   or    two behind: x2 is in MEM/WB, and is forwarded from there
#   add   three behind: x2 is written in the same cycle it is read
#   sw    four behind: x2 is simply in the register file
#
# At reset x3 (gp) points at the data and x1 (ra) just past the code, so x2
# comes out as an address the store can use.

        sub     x2, x3, x1
        and     x12, x2, x5
        or      x13, x6, x2
        add     x14, x2, x2
        sw      x15, 100(x2)
