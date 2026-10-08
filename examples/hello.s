# Prints a greeting, then exits.

        .data
msg:    .asciz  "Hello, RISC-V!\n"

        .text
main:
        la      a0, msg             # a0 = the address of the string
        li      a7, 4               # system call 4: print string
        ecall

        li      a7, 10              # system call 10: exit
        ecall
