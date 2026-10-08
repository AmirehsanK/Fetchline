# Computes 10! by recursion, to show a call stack at work: 3628800

        .text
main:
        li      a0, 10
        call    factorial
        li      a7, 1               # print int
        ecall
        li      a0, '\n'
        li      a7, 11              # print char
        ecall
        li      a7, 10              # exit
        ecall

# factorial(n): takes n in a0, returns n! in a0.
factorial:
        addi    sp, sp, -16         # a frame: the return address and n
        sw      ra, 12(sp)
        sw      a0, 8(sp)

        li      t0, 1
        ble     a0, t0, base        # n <= 1: the answer is 1

        addi    a0, a0, -1
        call    factorial           # a0 = (n - 1)!
        lw      t1, 8(sp)           # n, saved before the call
        mul     a0, a0, t1
        j       done
base:
        li      a0, 1
done:
        lw      ra, 12(sp)
        addi    sp, sp, 16
        ret
