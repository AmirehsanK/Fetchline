# The load-use hazard, as the textbooks draw it. The add needs x4 one cycle
# before the lw has read it from memory, so the pipeline has to wait a cycle;
# forwarding alone cannot fix this one.

        lw      x4, 0(x2)
        add     x5, x4, x6
        sub     x7, x5, x4
