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

#if defined(MAGIC_KERNEL_DIAG)
void magic_compile_diag(const char *phase) {
  printk(KERN_INFO "[spc-diag] compile: %s\n", phase ? phase : "(null)");
}
void magic_compile_diag_i2(const char *tag, int a, int b) {
  printk(KERN_INFO "[spc-diag] compile: %s %d %d\n", tag ? tag : "?", a, b);
}
#endif

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

static int kern_streq_ci(const char *a, const char *b) {
    while (*a && *b) {
        int ca = (*a >= 'A' && *a <= 'Z') ? *a + 32 : *a;
        int cb = (*b >= 'A' && *b <= 'Z') ? *b + 32 : *b;
        if (ca != cb) return 0;
        a++;
        b++;
    }
    return *a == *b;
}

/* True if s is "agiasm" (CI); optional for --output <format> */
static int kern_output_is_agiasm(const char *s) {
    return s && kern_streq_ci(s, "agiasm");
}

/* Parse --output agiasm / -o agiasm / --output=agiasm / one token "--output agiasm" */
static void kern_spc_parse_output_flag(int argc, char **argv, int *output_asm) {
    for (int i = 1; i < argc; i++) {
        char *arg = argv[i];
        if (!arg) continue;

        if (kern_streq(arg, "--agiasm")) {
            *output_asm = 1;
            continue;
        }

        if (kern_streq(arg, "--output") || kern_streq(arg, "-o")) {
            if (i + 1 < argc && kern_output_is_agiasm(argv[i + 1])) {
                *output_asm = 1;
                i++;
            }
            continue;
        }

        if (strncmp(arg, "--output=", 9) == 0 && kern_output_is_agiasm(arg + 9)) {
            *output_asm = 1;
            continue;
        }
        if (strncmp(arg, "-o=", 3) == 0 && kern_output_is_agiasm(arg + 3)) {
            *output_asm = 1;
            continue;
        }

        /* e.g. GUI terminal: one argv "--output agiasm" */
        if (strncmp(arg, "--output", 8) == 0 && arg[8] != '\0') {
            const char *v = arg + 8;
            if (*v == '=')
                v++;
            else
                while (*v == ' ' || *v == '\t') v++;
            if (kern_output_is_agiasm(v)) *output_asm = 1;
            continue;
        }
        if (arg[0] == '-' && arg[1] == 'o' && arg[2] != '\0' && arg[2] != '=') {
            const char *v = arg + 2;
            while (*v == ' ' || *v == '\t') v++;
            if (kern_output_is_agiasm(v)) *output_asm = 1;
        }
    }
}

/* UART-only diagnostics (survives GUI hook / panic); tag each line [spc-diag] */
static void spc_diag_safe_line(const char *tag, const char *s) {
    char buf[192];
    int ti = 0;
    while (tag[ti] && ti < (int)sizeof(buf) - 4) {
        buf[ti] = tag[ti];
        ti++;
    }
    buf[ti++] = ':';
    buf[ti++] = ' ';
    int i = 0;
    while (s && s[i] && ti < (int)sizeof(buf) - 2 && i < 120) {
        char c = s[i++];
        buf[ti++] = (c >= 32 && c < 127) ? c : '?';
    }
    buf[ti] = '\0';
    printk(KERN_INFO "[spc-diag] %s\n", buf);
}

static void spc_diag_argv_uart(int argc, char **argv) {
    printk(KERN_INFO "[spc-diag] argv_ptr=%p argc=%d\n", (void *)argv, argc);
    for (int i = 0; i < argc && i < 6; i++) {
        if (!argv[i]) {
            printk(KERN_INFO "[spc-diag] argv[%d]=NULL\n", i);
            continue;
        }
        printk(KERN_INFO "[spc-diag] argv[%d] ptr=%p\n", i, (void *)argv[i]);
        spc_diag_safe_line("  text", argv[i]);
    }
}

/* ------------------------------------------------------------------ */
/* RAMFS read without vfs_read_compat (fixes raw-data vs inode* mix)   */
/* ------------------------------------------------------------------ */

extern int ramfs_lookup_path_info(const char *path, size_t *out_size,
                                  int *out_is_dir, void **out_data);

/* Layout must match kernel/fs/ramfs.c struct ramfs_inode prefix through data */
typedef struct {
    uint64_t ino;
    uint32_t mode;
    uint32_t uid;
    uint32_t gid;
    uint32_t _pad;
    size_t size;
    uint8_t *data;
    size_t data_capacity;
} KernRamfsInodeLike;

/*
 * Copy file bytes from RAMFS into kmalloc'd buffer.
 * ramfs_lookup_path_info may set out_data to either inode* or raw file buffer;
 * we disambiguate without changing ramfs.c.
 */
static int kern_ramfs_read_bytes_alloc(const char *path, int null_terminate,
                                       unsigned char **out, size_t *out_len) {
    size_t sz = 0;
    int is_dir = 0;
    void *p = NULL;

    if (ramfs_lookup_path_info(path, &sz, &is_dir, &p) != 0 || is_dir || sz == 0 ||
        !p)
        return -1;

    const unsigned char *pb = (const unsigned char *)p;
    KernRamfsInodeLike *ino = (KernRamfsInodeLike *)p;
    const unsigned char *src;
    size_t n;

    /* .agic always starts with "AGIC" at the raw buffer base */
    if (sz >= 4 && pb[0] == 'A' && pb[1] == 'G' && pb[2] == 'I' && pb[3] == 'C') {
        src = pb;
        n = sz;
    } else if (ino->data != NULL &&
               (uintptr_t)ino->data != (uintptr_t)p && ino->size == sz &&
               (uintptr_t)ino->data > 0x1000ULL) {
        src = (const unsigned char *)ino->data;
        n = ino->size;
    } else {
        src = pb;
        n = sz;
    }

    size_t alloc_sz = null_terminate ? n + 1 : n;
    unsigned char *buf = (unsigned char *)_kmalloc(alloc_sz, 0);
    if (!buf)
        return -1;
    for (size_t i = 0; i < n; i++)
        buf[i] = src[i];
    if (null_terminate)
        buf[n] = '\0';
    *out = buf;
    *out_len = n;
    return 0;
}

/* Prefer RAMFS byte copy + compile_source (no fopen/vfs_read_compat). */
static int kern_magic_compile_file_safe(const char *path, CompileResult *out) {
    unsigned char *buf = NULL;
    size_t len = 0;
    if (kern_ramfs_read_bytes_alloc(path, 1, &buf, &len) == 0) {
        int ok = magic_compile_source((char *)buf, out);
        kfree(buf);
        return ok;
    }
    return magic_compile_file(path, out);
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
    printk(KERN_INFO "[spc-diag] === kern_spc_run enter ===\n");
    {
        static int spc_stdio_banner_once;
        if (!spc_stdio_banner_once) {
            spc_stdio_banner_once = 1;
            printk(KERN_INFO
                   "[spc-diag] spc stdio: fclose flushes via "
                   "ramfs_write_bytes_at_path (not legacy vfs_create)\n");
        }
    }
    spc_diag_argv_uart(argc, argv);

    /* Debug: show raw argv contents and lengths */
    magic_printf("\033[35m[spc-debug] argc=%d\033[0m\n", argc);
    for (int i = 0; i < argc; i++) {
        if (!argv[i]) {
            magic_printf("[spc-debug] argv[%d]=<NULL>\n", i);
            continue;
        }
        int len = 0;
        while (argv[i][len])
            len++;
        magic_printf("[spc-debug] argv[%d]=\"%s\" (len=%d)\n", i, argv[i], len);
        magic_printf("[spc-debug] argv[%d] bytes: ", i);
        for (int j = 0; j < len; j++) {
            magic_printf("%c", (j == 0 ? '[' : ' '));
            magic_printf("%c", argv[i][j]);
        }
        magic_printf("]\n");
    }

    if (argc < 2) {
        printk(KERN_INFO "[spc-diag] argc<2 usage\n");
        magic_printf("\033[33mUsage:\033[0m spc <file.agi> [--agiasm | --output agiasm]\n");
        magic_printf("  Compiles Magic language source to .agic or .agiasm\n");
        return 1;
    }

    const char *input_file = NULL;
    int         output_asm = 0;

    kern_spc_parse_output_flag(argc, argv, &output_asm);

    /* Last non-flag token except standalone "agiasm" (--output agiasm) */
    for (int i = 1; i < argc; i++) {
        if (!argv[i] || argv[i][0] == '\0') continue;
        if (argv[i][0] == '-') continue;
        if (kern_output_is_agiasm(argv[i])) continue;
        input_file = argv[i];
    }

    if (!input_file) {
        printk(KERN_INFO "[spc-diag] no input_file after parse\n");
        magic_printf("\033[31m[spc] No input file specified.\033[0m\n");
        return 1;
    }

    printk(KERN_INFO "[spc-diag] input_file ptr=%p output_asm=%d\n",
           (void *)input_file, output_asm);
    spc_diag_safe_line("input path", input_file);

    {
        vfs_node_t *vn = vfs_lookup(input_file);
        if (!vn) {
            printk(KERN_INFO "[spc-diag] vfs_lookup: NOT FOUND (before compile)\n");
        } else {
            printk(KERN_INFO
                   "[spc-diag] vfs_lookup: ok size=%llu internal=%p is_dir=%d\n",
                   (unsigned long long)vn->size, vn->internal,
                   vfs_is_dir(vn));
            vfs_close_handle(vn); /* return node_pool slot; fopen will lookup again */
        }
    }

    magic_printf("\033[36m[spc]\033[0m Compiling: %s\n", input_file);

    /* Compile - use static storage to avoid stack overflow (~142KB struct) */
    static CompileResult result;
    printk(KERN_INFO
           "[spc-diag] sizeof(CompileResult)=%zu result@%p (static)\n",
           sizeof(result), (void *)&result);
    printk(KERN_INFO "[spc-diag] calling kern_magic_compile_file_safe...\n");

    int compile_ok = kern_magic_compile_file_safe(input_file, &result);

    printk(KERN_INFO
           "[spc-diag] magic_compile_file returned %d success=%d err_line=%d\n",
           compile_ok, result.success, result.error_line);
    if (result.error[0]) {
        spc_diag_safe_line("compile error", result.error);
    }

    if (!compile_ok || !result.success) {
        magic_printf("\033[31m[spc] Compilation failed:\033[0m %s\n",
                     result.error[0] ? result.error : "(unknown error)");
        printk(KERN_INFO "[spc-diag] === kern_spc_run exit (compile fail) ===\n");
        return 1;
    }

    printk(KERN_INFO
           "[spc-diag] unit: proc_count=%d func_count=%d entry_point.count=%d\n",
           result.unit.proc_count, result.unit.func_count,
           result.unit.entry_point.count);

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
    spc_diag_safe_line("output path", output_file);

    int ok;
    if (output_asm) {
        printk(KERN_INFO "[spc-diag] magic_save_agiasm...\n");
        ok = magic_save_agiasm(&result.unit, output_file);
    } else {
        printk(KERN_INFO "[spc-diag] magic_save_agic...\n");
        ok = magic_save_agic(&result.unit, output_file);
    }
    printk(KERN_INFO "[spc-diag] save returned ok=%d\n", ok);

    /* Free compiled blocks */
    printk(KERN_INFO "[spc-diag] block_free procedures (%d)...\n",
           result.unit.proc_count);
    for (int i = 0; i < result.unit.proc_count; i++)
        block_free(&result.unit.procedures[i].body);
    printk(KERN_INFO "[spc-diag] block_free functions (%d)...\n",
           result.unit.func_count);
    for (int i = 0; i < result.unit.func_count; i++)
        block_free(&result.unit.functions[i].body);
    printk(KERN_INFO "[spc-diag] block_free entry_point...\n");
    block_free(&result.unit.entry_point);
    printk(KERN_INFO "[spc-diag] all block_free done\n");

    if (!ok) {
        magic_printf("\033[31m[spc] Failed to write output file.\033[0m\n");
        printk(KERN_INFO "[spc-diag] === kern_spc_run exit (save fail) ===\n");
        return 1;
    }

    magic_printf("\033[32m[spc] Compilation successful!\033[0m\n");
    magic_printf("  Input:  %s\n", input_file);
    magic_printf("  Output: %s\n", output_file);
    printk(KERN_INFO "[spc-diag] === kern_spc_run exit OK ===\n");
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
        unsigned char *bin = NULL;
        size_t blen = 0;
        int loaded = 0;
        if (kern_ramfs_read_bytes_alloc(input_file, 0, &bin, &blen) == 0) {
            loaded = magic_load_agic_from_buffer(bin, blen, &unit);
            kfree(bin);
        }
        if (!loaded && !magic_load_agic(input_file, &unit)) {
            magic_printf("\033[31m[spe] Failed to load bytecode.\033[0m\n");
            return 1;
        }
    } else if (kern_has_ext(input_file, ".agiasm")) {
        magic_printf("\033[36m[spe]\033[0m Assembling: %s\n", input_file);

        unsigned char *raw = NULL;
        size_t rlen = 0;
        if (kern_ramfs_read_bytes_alloc(input_file, 1, &raw, &rlen) != 0) {
            magic_printf("\033[31m[spe] Cannot open: %s\033[0m\n", input_file);
            return 1;
        }
        char *src = (char *)raw;

        /* Zero cr */
        {
            unsigned char *p = (unsigned char *)&cr;
            for (size_t i = 0; i < sizeof(cr); i++) p[i] = 0;
        }
        if (!magic_compile_source(src, &cr) || !cr.success) {
            magic_printf("\033[31m[spe] Assembly error: %s\033[0m\n", cr.error);
            kfree(raw);
            return 1;
        }
        kfree(raw);
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
        if (!kern_magic_compile_file_safe(input_file, &cr) || !cr.success) {
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
