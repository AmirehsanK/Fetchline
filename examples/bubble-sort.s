# Sorts an array in place with bubble sort and prints it: 1 2 3 5 7 8 9

        .data
array:  .word   5, 2, 9, 1, 7, 3, 8
        .equ    COUNT, (. - array) / 4

        .text
main:
        la      s0, array
        li      s1, COUNT - 1       # comparisons in the first pass
outer:
        blez    s1, print
        mv      t0, s0              # p = array
        mv      t1, s1              # comparisons left in this pass
inner:
        lw      t2, 0(t0)
        lw      t3, 4(t0)
        ble     t2, t3, ordered
        sw      t3, 0(t0)           # out of order: swap the pair
        sw      t2, 4(t0)
ordered:
        addi    t0, t0, 4
        addi    t1, t1, -1
        bnez    t1, inner

        addi    s1, s1, -1          # the largest is now at the end; the next pass is one shorter
        j       outer

print:
        li      s1, COUNT
show:
        lw      a0, 0(s0)
        li      a7, 1               # print int
        ecall
        li      a0, ' '
        li      a7, 11              # print char
        ecall
        addi    s0, s0, 4
        addi    s1, s1, -1
        bnez    s1, show

        li      a0, '\n'
        li      a7, 11
        ecall
        li      a7, 10              # exit
        ecall
