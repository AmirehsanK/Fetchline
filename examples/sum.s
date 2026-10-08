# Adds the numbers 1 to 10 and prints the total: 55

        .text
main:
        li      a0, 0               # total
        li      t0, 1               # i
        li      t1, 10              # limit
loop:
        add     a0, a0, t0          # total += i
        addi    t0, t0, 1           # i++
        ble     t0, t1, loop        # while (i <= limit)

        li      a7, 1               # print int: a0 already holds the total
        ecall
        li      a0, '\n'
        li      a7, 11              # print char
        ecall

        li      a7, 10              # exit
        ecall
