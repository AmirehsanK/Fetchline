# Adds up the same table a column at a time, which a cache does not like: 2080
#
# This is cache-rows.s with its two loops changed over. Going down a column, each word is a
# whole row of 32 bytes on from the one just read: never in the same block, and in a small
# cache each block lands on the one that was brought in a moment ago and puts it out.
#
# With dcache at 4x1x16:10 every one of the 64 loads misses; cache-rows.s misses 16 times.
# Give it a cache the whole table fits in, 16x1x16:10, and the two take the same time again.

        .data
table:  .word    1,  2,  3,  4,  5,  6,  7,  8
        .word    9, 10, 11, 12, 13, 14, 15, 16
        .word   17, 18, 19, 20, 21, 22, 23, 24
        .word   25, 26, 27, 28, 29, 30, 31, 32
        .word   33, 34, 35, 36, 37, 38, 39, 40
        .word   41, 42, 43, 44, 45, 46, 47, 48
        .word   49, 50, 51, 52, 53, 54, 55, 56
        .word   57, 58, 59, 60, 61, 62, 63, 64
        .equ    ROWS, 8
        .equ    COLUMNS, 8

        .text
main:
        la      s0, table
        li      s1, ROWS
        li      s2, COLUMNS
        li      a0, 0               # the total
        li      t1, 0               # column
column:
        li      t0, 0               # row
row:
        slli    t2, t0, 3           # row * 8 words in a row
        add     t2, t2, t1          # + column
        slli    t2, t2, 2           # * 4 bytes in a word
        add     t2, t2, s0
        lw      t3, 0(t2)           # table[row][column]
        add     a0, a0, t3

        addi    t0, t0, 1           # the next word of this column: a whole row further on
        blt     t0, s1, row
        addi    t1, t1, 1
        blt     t1, s2, column

        li      a7, 1               # print int: a0 holds the total
        ecall
        li      a0, '\n'
        li      a7, 11              # print char
        ecall

        li      a7, 10              # exit
        ecall
