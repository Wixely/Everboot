/* Everboot original MBR, MIT. Loads the first partition's boot sector using
 * BIOS extended reads. The generated disk always places that partition at 63.
 */
.intel_syntax noprefix
.code16
.global _start
_start:
    cli
    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov sp, 0x7c00
    cld
    mov si, 0x7c00
    mov di, 0x600
    mov cx, 256
    rep movsw
    ljmp 0, relocated
relocated:
    sti
    mov si, offset packet
    mov ah, 0x42
    int 0x13
    jc failed
    cmp word ptr [0x7dfe], 0xaa55
    jne failed
    ljmp 0, 0x7c00
failed:
    int 0x18
    cli
    hlt
    jmp failed
.balign 4
packet:
    .byte 16, 0
    .word 1, 0x7c00, 0
    .quad 63
.org 510
    .word 0xaa55
