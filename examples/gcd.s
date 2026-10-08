# The greatest common divisor of 1071 and 462, by Euclid's algorithm: 21

        .text
main:
        li      a0, 1071
        li      a1, 462
loop:
        beqz    a1, done
        remu    t0, a0, a1          # a, b = b, a mod b
        mv      a0, a1
        mv      a1, t0
        j       loop
done:
        li      a7, 1               # print int
        ecall
        li      a0, '\n'
        li      a7, 11              # print char
        ecall
        li      a7, 10              # exit
        ecall
