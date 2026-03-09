/*
 * spe.c - Space Emulator (Magic Language Interpreter)
 *
 * Command: spe <file> [--verbose]
 *
 * Executes Magic programs from:
 *   .agi    - source code (compiles then runs)
 *   .agic   - binary bytecode
 *   .agiasm - text assembly
 *
 * This is a C port of Magic_Kernel_Dotnet/Space.OS.Simulator/Program.cs
 * and Magic_Kernel_Dotnet/Magic.Kernel/Interpretation/Interpreter.cs
 */

#include "magic.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* Terminal colors (ANSI) */
#define COLOR_RESET   "\033[0m"
#define COLOR_GREEN   "\033[32m"
#define COLOR_RED     "\033[31m"
#define COLOR_YELLOW  "\033[33m"
#define COLOR_CYAN    "\033[36m"
#define COLOR_BOLD    "\033[1m"
#define COLOR_GRAY    "\033[90m"
#define COLOR_MAGENTA "\033[35m"

static int use_color = 1;

static void print_sep(void)
{
    printf("%s───────────────────────────────────────────%s\n",
           use_color ? COLOR_GRAY : "", use_color ? COLOR_RESET : "");
}

static void print_header(void)
{
    printf("\n");
    print_sep();
    printf("%s%s  spe - Space Emulator (Magic Language)%s\n",
           use_color ? COLOR_BOLD : "",
           use_color ? COLOR_MAGENTA : "",
           use_color ? COLOR_RESET : "");
    print_sep();
    printf("\n");
}

static void print_usage(void)
{
    printf("Usage: spe <file> [--verbose]\n");
    printf("\n");
    printf("  <file>     Magic program file to execute\n");
    printf("             Supported: .agi (source), .agic (bytecode), .agiasm (assembly)\n");
    printf("  --verbose  Enable execution tracing\n");
    printf("\n");
}

static void print_info(const char *msg)
{
    printf("%s  [*]%s %s\n",
           use_color ? COLOR_CYAN : "",
           use_color ? COLOR_RESET : "",
           msg);
}

static void print_success(const char *msg)
{
    printf("%s%s  [OK]%s %s\n",
           use_color ? COLOR_BOLD : "",
           use_color ? COLOR_GREEN : "",
           use_color ? COLOR_RESET : "",
           msg);
}

static void print_error(const char *msg)
{
    fprintf(stderr, "%s%s  [ERR]%s %s\n",
            use_color ? COLOR_BOLD : "",
            use_color ? COLOR_RED : "",
            use_color ? COLOR_RESET : "",
            msg);
}

static int has_ext(const char *filename, const char *ext)
{
    const char *dot = strrchr(filename, '.');
    if (!dot) return 0;
    int i = 0;
    while (dot[i] && ext[i]) {
        if (tolower((unsigned char)dot[i]) != tolower((unsigned char)ext[i]))
            return 0;
        i++;
    }
    return dot[i] == ext[i];
}

static char *read_file(const char *path)
{
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    char *buf = malloc(sz + 1);
    if (!buf) { fclose(f); return NULL; }
    if (fread(buf, 1, sz, f) != (size_t)sz) {
        free(buf); fclose(f); return NULL;
    }
    buf[sz] = '\0';
    fclose(f);
    return buf;
}

/* Use a static (BSS) ExecutableUnit to avoid stack overflow (struct is ~141KB) */
static ExecutableUnit g_unit;
static CompileResult  g_result;

int main(int argc, char *argv[])
{
    use_color = 1;

    print_header();

    if (argc < 2) {
        print_usage();
        return 1;
    }

    const char *input_file = NULL;
    int         verbose     = 0;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--verbose") == 0 || strcmp(argv[i], "-v") == 0) {
            verbose = 1;
        } else if (strcmp(argv[i], "--no-color") == 0) {
            use_color = 0;
        } else if (argv[i][0] != '-') {
            input_file = argv[i];
        }
    }

    if (!input_file) {
        print_error("No input file specified.");
        print_usage();
        return 1;
    }

    ExecutableUnit *unit = &g_unit;
    CompileResult  *cr   = &g_result;

    memset(unit, 0, sizeof(*unit));
    block_init(&unit->entry_point);

    /* Load program depending on file extension */
    if (has_ext(input_file, ".agic")) {
        /* Binary bytecode */
        {
            char info[512];
            snprintf(info, sizeof(info), "Loading bytecode: %s", input_file);
            print_info(info);
        }
        if (!magic_load_agic(input_file, unit)) {
            char err[512];
            snprintf(err, sizeof(err),
                     "Failed to load bytecode file: %s", input_file);
            print_error(err);
            return 1;
        }
    }
    else if (has_ext(input_file, ".agiasm")) {
        /* Text assembly - compile it */
        {
            char info[512];
            snprintf(info, sizeof(info), "Loading assembly: %s", input_file);
            print_info(info);
        }
        char *src = read_file(input_file);
        if (!src) {
            char err[512];
            snprintf(err, sizeof(err), "Cannot open file: %s", input_file);
            print_error(err);
            return 1;
        }
        if (!magic_compile_source(src, cr) || !cr->success) {
            char err[512];
            snprintf(err, sizeof(err),
                     "Assembly parse error: %.480s", cr->error);
            print_error(err);
            free(src);
            return 1;
        }
        free(src);
        *unit = cr->unit;
    }
    else {
        /* .agi source or unknown - compile then run */
        if (!has_ext(input_file, ".agi")) {
            char warn[512];
            snprintf(warn, sizeof(warn),
                     "Unrecognized extension, treating as .agi: %s",
                     input_file);
            print_info(warn);
        }

        {
            char info[512];
            snprintf(info, sizeof(info), "Compiling source: %s", input_file);
            print_info(info);
        }

        if (!magic_compile_file(input_file, cr) || !cr->success) {
            char err[512];
            snprintf(err, sizeof(err),
                     "Compilation error: %.480s", cr->error);
            print_error(err);
            return 1;
        }
        *unit = cr->unit;
    }

    /* Run the program */
    {
        char info[512];
        snprintf(info, sizeof(info), "Executing: %s", input_file);
        print_info(info);
    }
    print_sep();
    printf("\n");

    Interpreter interp;
    interpreter_init(&interp);
    interp.verbose = verbose;

    int ok = interpreter_run(&interp, unit);

    printf("\n");
    print_sep();

    /* Free blocks */
    for (int i = 0; i < unit->proc_count; i++)
        block_free(&unit->procedures[i].body);
    for (int i = 0; i < unit->func_count; i++)
        block_free(&unit->functions[i].body);
    block_free(&unit->entry_point);

    if (ok) {
        print_success("Execution completed.");
    } else {
        print_error("Execution failed.");
        return 1;
    }

    printf("\n");
    return 0;
}
