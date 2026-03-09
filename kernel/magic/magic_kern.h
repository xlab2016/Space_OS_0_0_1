/*
 * magic_kern.h - Kernel-internal Magic language runtime API
 *
 * Exposes kernel-internal spc (compiler) and spe (interpreter) functions
 * that can be called directly from terminal.c without spawning an ELF process.
 *
 * This solves the GUI freeze issue where process_exec_args() would block
 * waiting for a musl ELF that hung due to kapi-ABI vs Linux ABI mismatch.
 */

#ifndef MAGIC_KERN_H
#define MAGIC_KERN_H

#include "../include/types.h"

/**
 * kern_magic_set_output_hook - Set output hook for magic tool output
 * @hook: Callback receiving (buf, len) for each chunk of output.
 *        Set to NULL to restore default printk/UART output.
 *
 * Call this before kern_spc_run() / kern_spe_run() to redirect Magic
 * output to the GUI terminal buffer.
 */
void kern_magic_set_output_hook(void (*hook)(const char *buf, size_t len));

/**
 * kern_spc_run - Run the Magic compiler (spc) internally
 * @argc: Number of arguments
 * @argv: argv[0]="spc", argv[1]=<file.agi>, [argv[2]="--agiasm"]
 *
 * Returns 0 on success, non-zero on failure.
 * Output (compiler messages and errors) goes through the output hook.
 */
int kern_spc_run(int argc, char **argv);

/**
 * kern_spe_run - Run the Magic interpreter (spe) internally
 * @argc: Number of arguments
 * @argv: argv[0]="spe", argv[1]=<file>, [argv[2]="--verbose"]
 *
 * Supports .agi (source), .agic (bytecode), .agiasm (text assembly).
 * Returns 0 on success, non-zero on failure.
 * Output (program output and errors) goes through the output hook.
 */
int kern_spe_run(int argc, char **argv);

#endif /* MAGIC_KERN_H */
