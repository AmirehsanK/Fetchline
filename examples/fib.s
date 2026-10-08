# Prints the first ten Fibonacci numbers: 0 1 1 2 3 5 8 13 21 34

        .text
main:
        li      s0, 0               # a = F(n)
        li      s1, 1               # b = F(n + 1)
        li      s2, 10              # how many are left to print
next:
        mv      a0, s0
        li      a7, 1               # print int
        ecall
        li      a0, ' '
        li      a7, 11              # print char
        ecall

        add     t0, s0, s1          # a, b = b, a + b
        mv      s0, s1
        mv      s1, t0

        addi    s2, s2, -1
        bnez    s2, next

        li      a0, '\n'
        li      a7, 11
        ecall
        li      a7, 10              # exit
        ecall
