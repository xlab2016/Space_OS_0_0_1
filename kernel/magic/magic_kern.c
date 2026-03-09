/*
 * magic_kern.c - Kernel-internal Magic language runtime
 *
 * Compiles the Magic language compiler (spc) and interpreter (spe) directly
 * into the kernel, bypassing the ELF process execution path that caused GUI
 * freezes when running musl-linked userspace ELFs via process_exec_args().
 *
 * Root cause of the original bug:
 *   - /bin/spc and /bin/spe are musl libc ELFs with standard main(argc, argv)
 *   - process_entry_wrapper() calls them as entry(kapi, argc, argv) -- kapi-ABI
 *   - musl's _start does not expect kapi as first arg; it hangs or crashes
 *   - process_exec_args() busy-waits for process exit, freezing the GUI
 *
 * Solution (Option 1 from the issue):
 *   Build the Magic compiler/interpreter as kernel-internal functions.
 *   terminal.c calls kern_spc_run() / kern_spe_run() directly -- no process
 *   spawning, no ABI mismatch, no GUI freeze.
 *
 * The magic_stdio_shim.h header maps stdio/libc calls to kernel equivalents:
 *   malloc/free/realloc  -> kmalloc/kfree/krealloc
 *   printf               -> printk (or magic_output_hook for terminal)
 *   fprintf(stderr, ...) -> printk
 *   fopen/fclose/...     -> VFS compat layer
 *   tolower/isdigit/...  -> inline kernel versions
 */

/* Include the shim BEFORE any standard headers to intercept them */
#include "magic_stdio_shim.h"

/* Now include the Magic language sources directly.
 * They see malloc/printf/fopen/etc. as the kernel shim versions. */
#include "../../userspace/magic/magic.h"

/* Include Magic implementation files.
 * Each file includes magic.h + standard headers; the shim redirects those. */
#include "../../userspace/magic/scanner.c"
#include "../../userspace/magic/compiler.c"
#include "../../userspace/magic/interpreter.c"

/* ------------------------------------------------------------------ */
/* Output hook: terminal.c sets this before calling kern_spc/spe_run  */
/* to redirect Magic output to the GUI terminal buffer.               */
/* ------------------------------------------------------------------ */

void (*magic_output_hook)(const char *buf, size_t len) = NULL;

/**
 * kern_magic_set_output_hook - Set terminal output hook for magic tools
 * @hook: Function to call with each chunk of output, or NULL for UART/printk
 *
 * Called by terminal.c before running spc/spe to redirect output to the
 * GUI terminal buffer. Set to NULL after the call to restore normal output.
 */
void kern_magic_set_output_hook(void (*hook)(const char *buf, size_t len)) {
    magic_output_hook = hook;
}

/* ------------------------------------------------------------------ */
/* Internal helper: check if filename ends with ext (case-insensitive) */
/* ------------------------------------------------------------------ */

static int kern_has_ext(const char *fn, const char *ext) {
    int len = 0;
    while (fn[len]) len++;
    int dot = -1;
    for (int i = len - 1; i >= 0; i--) {
        if (fn[i] == '.') { dot = i; break; }
        if (fn[i] == '/') break;
    }
    if (dot < 0) return 0;
    const char *a = fn + dot;
    const char *b = ext;
    while (*a && *b) {
        int ca = (*a >= 'A' && *a <= 'Z') ? *a + 32 : *a;
        int cb = (*b >= 'A' && *b <= 'Z') ? *b + 32 : *b;
        if (ca != cb) return 0;
        a++; b++;
    }
    return *a == *b;
}

/* ------------------------------------------------------------------ */
/* Internal helper: strcmp for argument matching                       */
/* ------------------------------------------------------------------ */

static int kern_streq(const char *a, const char *b) {
    while (*a && *b && *a == *b) { a++; b++; }
    return *a == *b;
}

/* ------------------------------------------------------------------ */
/* kern_spc_run - Kernel-internal Magic compiler                      */
/* ------------------------------------------------------------------ */

/**
 * kern_spc_run - Compile a Magic source file (.agi) to bytecode
 * @argc: Argument count (same as userspace spc's argc)
 * @argv: Argument vector: argv[0]="spc", argv[1]=input_file, [argv[2]=flags]
 *
 * Implements the spc command logic directly in the kernel.
 * Returns 0 on success, non-zero on failure.
 *
 * The output file is written to VFS alongside the input file.
 * All text output goes through magic_output_hook (GUI terminal) or printk.
 */
int kern_spc_run(int argc, char **argv) {
    if (argc < 2) {
        magic_printf("\033[33mUsage:\033[0m spc <file.agi> [--agiasm]\n");
        magic_printf("  Compiles Magic language source to .agic bytecode\n");
        return 1;
    }

    const char *input_file = NULL;
    int         output_asm = 0;

    for (int i = 1; i < argc; i++) {
        if (kern_streq(argv[i], "--agiasm")) {
            output_asm = 1;
        } else if (argv[i][0] != '-') {
            input_file = argv[i];
        }
    }

    if (!input_file) {
        magic_printf("\033[31m[spc] No input file specified.\033[0m\n");
        return 1;
    }

    magic_printf("\033[36m[spc]\033[0m Compiling: %s\n", input_file);

    /* Compile - use static storage to avoid stack overflow (~142KB struct) */
    static CompileResult result;
    if (!magic_compile_file(input_file, &result) || !result.success) {
        magic_printf("\033[31m[spc] Compilation failed:\033[0m %s\n",
                     result.error[0] ? result.error : "(unknown error)");
        return 1;
    }

    /* Build output filename by replacing extension */
    char output_file[512];
    {
        int len = 0;
        while (input_file[len] && len < 510) { output_file[len] = input_file[len]; len++; }
        output_file[len] = '\0';

        /* Find last dot (but not past last slash) */
        int dot = -1;
        for (int i = len - 1; i >= 0; i--) {
            if (output_file[i] == '.') { dot = i; break; }
            if (output_file[i] == '/') break;
        }
        int base_len = (dot >= 0) ? dot : len;

        const char *ext = output_asm ? ".agiasm" : ".agic";
        int ext_len = 0;
        while (ext[ext_len]) ext_len++;
        if (base_len + ext_len < 511) {
            for (int i = 0; i < ext_len; i++) output_file[base_len + i] = ext[i];
            output_file[base_len + ext_len] = '\0';
        }
    }

    magic_printf("\033[36m[spc]\033[0m Writing: %s\n", output_file);

    int ok;
    if (output_asm) {
        ok = magic_save_agiasm(&result.unit, output_file);
    } else {
        ok = magic_save_agic(&result.unit, output_file);
    }

    /* Free compiled blocks */
    for (int i = 0; i < result.unit.proc_count; i++)
        block_free(&result.unit.procedures[i].body);
    for (int i = 0; i < result.unit.func_count; i++)
        block_free(&result.unit.functions[i].body);
    block_free(&result.unit.entry_point);

    if (!ok) {
        magic_printf("\033[31m[spc] Failed to write output file.\033[0m\n");
        return 1;
    }

    magic_printf("\033[32m[spc] Compilation successful!\033[0m\n");
    magic_printf("  Input:  %s\n", input_file);
    magic_printf("  Output: %s\n", output_file);
    return 0;
}

/* ------------------------------------------------------------------ */
/* kern_spe_run - Kernel-internal Magic interpreter                   */
/* ------------------------------------------------------------------ */

/**
 * kern_spe_run - Run a Magic program file (.agi/.agic/.agiasm)
 * @argc: Argument count
 * @argv: Argument vector: argv[0]="spe", argv[1]=file, [argv[2]=--verbose]
 *
 * Implements the spe command logic directly in the kernel.
 * Returns 0 on success, non-zero on failure.
 */
int kern_spe_run(int argc, char **argv) {
    if (argc < 2) {
        magic_printf("\033[33mUsage:\033[0m spe <file> [--verbose]\n");
        magic_printf("  Runs Magic programs (.agi, .agic, .agiasm)\n");
        return 1;
    }

    const char *input_file = NULL;
    int         verbose    = 0;

    for (int i = 1; i < argc; i++) {
        if (kern_streq(argv[i], "--verbose") || kern_streq(argv[i], "-v")) {
            verbose = 1;
        } else if (argv[i][0] != '-') {
            input_file = argv[i];
        }
    }

    if (!input_file) {
        magic_printf("\033[31m[spe] No input file specified.\033[0m\n");
        return 1;
    }

    /* Use static storage to avoid stack overflow for large structs */
    static ExecutableUnit unit;
    static CompileResult  cr;

    /* Zero unit */
    {
        unsigned char *p = (unsigned char *)&unit;
        for (size_t i = 0; i < sizeof(unit); i++) p[i] = 0;
    }
    block_init(&unit.entry_point);

    if (kern_has_ext(input_file, ".agic")) {
        magic_printf("\033[36m[spe]\033[0m Loading bytecode: %s\n", input_file);
        if (!magic_load_agic(input_file, &unit)) {
            magic_printf("\033[31m[spe] Failed to load bytecode.\033[0m\n");
            return 1;
        }
    } else if (kern_has_ext(input_file, ".agiasm")) {
        magic_printf("\033[36m[spe]\033[0m Assembling: %s\n", input_file);

        /* Load .agiasm text via VFS */
        vfs_node_t *node = vfs_lookup(input_file);
        if (!node) {
            magic_printf("\033[31m[spe] Cannot open: %s\033[0m\n", input_file);
            return 1;
        }
        char *src = (char *)_kmalloc(node->size + 1, 0);
        if (!src) {
            magic_printf("\033[31m[spe] Out of memory.\033[0m\n");
            return 1;
        }
        int bytes = vfs_read_compat(node, src, node->size, 0);
        if (bytes < 0) bytes = 0;
        src[bytes] = '\0';

        /* Zero cr */
        {
            unsigned char *p = (unsigned char *)&cr;
            for (size_t i = 0; i < sizeof(cr); i++) p[i] = 0;
        }
        if (!magic_compile_source(src, &cr) || !cr.success) {
            magic_printf("\033[31m[spe] Assembly error: %s\033[0m\n", cr.error);
            kfree(src);
            return 1;
        }
        kfree(src);
        unit = cr.unit;
    } else {
        /* .agi source (or unknown extension -- treat as .agi) */
        if (!kern_has_ext(input_file, ".agi")) {
            magic_printf("\033[36m[spe]\033[0m Treating as .agi source: %s\n", input_file);
        } else {
            magic_printf("\033[36m[spe]\033[0m Compiling: %s\n", input_file);
        }

        /* Zero cr */
        {
            unsigned char *p = (unsigned char *)&cr;
            for (size_t i = 0; i < sizeof(cr); i++) p[i] = 0;
        }
        if (!magic_compile_file(input_file, &cr) || !cr.success) {
            magic_printf("\033[31m[spe] Compilation error: %s\033[0m\n", cr.error);
            return 1;
        }
        unit = cr.unit;
    }

    magic_printf("\033[36m[spe]\033[0m Executing: %s\n", input_file);

    Interpreter interp;
    interpreter_init(&interp);
    interp.verbose = verbose;

    int ok = interpreter_run(&interp, &unit);

    /* Free compiled blocks */
    for (int i = 0; i < unit.proc_count; i++)
        block_free(&unit.procedures[i].body);
    for (int i = 0; i < unit.func_count; i++)
        block_free(&unit.functions[i].body);
    block_free(&unit.entry_point);

    if (ok) {
        magic_printf("\033[32m[spe] Execution complete.\033[0m\n");
        return 0;
    } else {
        magic_printf("\033[31m[spe] Execution failed.\033[0m\n");
        return 1;
    }
}
