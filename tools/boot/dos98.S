/* Everboot's FAT12 bootstrap. Original code, MIT license.
 * Fixed 1.44 MiB layout, contiguous IO.SYS at cluster 2. The builder owns
 * this layout; this is deliberately not a general-purpose FAT bootloader.
 * DOS 7.1 MSLOAD ABI: BP=7c00, DI=first cluster, data LBA at BP-4,
 * original INT 1e vector on stack, loader at 0070:0200.
 */
.intel_syntax noprefix
.code16
.global _start
_start:
    jmp start
    nop
    .ascii "EVERBOOT"
    .word 512
    .byte 1
    .word 1
    .byte 2
    .word 224, 2880
    .byte 0xf0
    .word 9, 18, 2
    .long 0, 0
    .byte 0, 0, 0x29
    .long 0x45564231
    .ascii "EVERBOOT   "
    .ascii "FAT12   "
start:
    cli
    xor ax, ax
    mov ss, ax
    mov sp, 0x7bf0
    mov ds, ax
    mov es, ax
    cld
    sti
    mov bp, 0x7c00
    mov byte ptr [bp+36], dl
    mov dword ptr [bp-4], 33
    /* Preserve the BIOS floppy parameter vector for MSLOAD's error path. */
    push word ptr [0x7a]
    push word ptr [0x78]
    push 0
    push 0x78
    mov si, 33
    mov bx, 0x700
read_sector:
    mov ax, si
    xor dx, dx
    mov cx, 18
    div cx
    mov cl, dl
    inc cl
    xor dx, dx
    mov di, 2
    div di
    mov ch, al
    mov dh, dl
    mov dl, byte ptr [bp+36]
    mov ax, 0x0201
    push si
    push bx
    int 0x13
    pop bx
    pop si
    jc failed
    add bx, 512
    inc si
    cmp si, 37
    jne read_sector
    cmp word ptr [0x700], 0x5a4d
    jne failed
    cmp word ptr [0x900], 0x4a42
    jne failed
    mov di, 2
    mov si, offset messages
    ljmp 0x70, 0x200
failed:
    mov si, offset failure
print:
    lodsb
    test al, al
    jz halt
    mov ah, 0x0e
    mov bx, 7
    int 0x10
    jmp print
halt:
    cli
    hlt
    jmp halt
failure:
    .asciz "Everboot: invalid DOS startup files or disk read error."
/* MSLOAD consumes a compact message table via BP+0x1ee. */
messages:
    .byte 1
    .ascii "Everboot DOS loader error"
    .byte 0xff, 0
.org 0x1ee
    .word messages
.org 510
    .word 0xaa55
