# Counts the primes below 10000 with the sieve of Eratosthenes: 1229

        .equ    LIMIT, 10000

        .bss
sieve:  .zero   LIMIT               # sieve[i] is set once i is known to be composite

        .text
main:
        la      s0, sieve
        li      s1, LIMIT
        li      s2, 0               # how many primes so far
        li      t0, 2               # i
outer:
        bge     t0, s1, done
        add     t1, s0, t0
        lbu     t2, 0(t1)
        bnez    t2, next            # crossed out already: not a prime

        addi    s2, s2, 1           # i is a prime
        mul     t3, t0, t0          # every smaller multiple of i has a smaller factor
        li      t5, 1
inner:
        bge     t3, s1, next
        add     t4, s0, t3
        sb      t5, 0(t4)           # cross out i * i, i * i + i, ...
        add     t3, t3, t0
        j       inner
next:
        addi    t0, t0, 1
        j       outer

done:
        mv      a0, s2
        li      a7, 1               # print int
        ecall
        li      a0, '\n'
        li      a7, 11              # print char
        ecall
        li      a7, 10              # exit
        ecall
