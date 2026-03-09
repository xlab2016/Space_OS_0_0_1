/* Linker script: 16-bit BIOS-style code as runnable ELF (load at 0x7C00) */
OUTPUT_FORMAT("elf32-i386")
OUTPUT_ARCH(i386)
ENTRY(_start)

PHDRS
{
    text PT_LOAD FLAGS(5);   /* PF_R | PF_X */
    data PT_LOAD FLAGS(6);   /* PF_R | PF_W */
}

SECTIONS
{
    . = 0x7C00;
    .text : { *(.text) } :text
    .data : { *(.data) } :data
    /DISCARD/ : { *(.note*) *(.comment) }
}
