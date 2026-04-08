/*
 * spc.c - Space Compiler (Magic Language Compiler)
 *
 * Command: spc <file.agi> [--agiasm | --output agiasm | -o agiasm ...]
 *
 * Compiles a .agi Magic source file into:
 *   .agic  - binary bytecode (default)
 *   .agiasm - text assembly (with --agiasm flag)
 *
 * This is a C port of Magic_Kernel_Dotnet/Magic.Compiler/Program.cs
 * and Magic_Kernel_Dotnet/Magic.Kernel/Compilation/Compiler.cs
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
    printf("%s%s  spc - Space Compiler (Magic Language)%s\n",
           use_color ? COLOR_BOLD : "",
           use_color ? COLOR_CYAN : "",
           use_color ? COLOR_RESET : "");
    print_sep();
    printf("\n");
}

static int str_eq_ci(const char *a, const char *b)
{
    while (*a && *b) {
        int ca = (*a >= 'A' && *a <= 'Z') ? *a + 32 : *a;
        int cb = (*b >= 'A' && *b <= 'Z') ? *b + 32 : *b;
        if (ca != cb) return 0;
        a++;
        b++;
    }
    return *a == *b;
}

static int output_name_is_agiasm(const char *s)
{
    return s && str_eq_ci(s, "agiasm");
}

static void parse_output_flags(int argc, char **argv, int *output_agiasm)
{
    for (int i = 1; i < argc; i++) {
        char *arg = argv[i];
        if (!arg) continue;

        if (strcmp(arg, "--agiasm") == 0) {
            *output_agiasm = 1;
            continue;
        }
        if (strcmp(arg, "--output") == 0 || strcmp(arg, "-o") == 0) {
            if (i + 1 < argc && output_name_is_agiasm(argv[i + 1])) {
                *output_agiasm = 1;
                i++;
            }
            continue;
        }
        if (strncmp(arg, "--output=", 9) == 0 && output_name_is_agiasm(arg + 9)) {
            *output_agiasm = 1;
            continue;
        }
        if (strncmp(arg, "-o=", 3) == 0 && output_name_is_agiasm(arg + 3)) {
            *output_agiasm = 1;
            continue;
        }
        if (strncmp(arg, "--output", 8) == 0 && arg[8] != '\0') {
            const char *v = arg + 8;
            if (*v == '=') v++;
            else while (*v == ' ' || *v == '\t') v++;
            if (output_name_is_agiasm(v)) *output_agiasm = 1;
            continue;
        }
        if (arg[0] == '-' && arg[1] == 'o' && arg[2] != '\0' && arg[2] != '=') {
            const char *v = arg + 2;
            while (*v == ' ' || *v == '\t') v++;
            if (output_name_is_agiasm(v)) *output_agiasm = 1;
        }
    }
}

static void print_usage(void)
{
    printf("Usage: spc <file.agi> [--agiasm | --output agiasm | -o agiasm ...]\n");
    printf("\n");
    printf("  <file.agi>        Magic source file to compile\n");
    printf("  --agiasm          Output text assembly instead of binary\n");
    printf("  --output agiasm   Same (also: -o agiasm, --output=agiasm)\n");
    printf("\n");
    printf("Output:\n");
    printf("  .agic    Binary bytecode (default)\n");
    printf("  .agiasm  Text assembly listing\n");
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

static void print_warning(const char *msg)
{
    fprintf(stderr, "%s  [!]%s %s\n",
            use_color ? COLOR_YELLOW : "",
            use_color ? COLOR_RESET : "",
            msg);
}

/* Replace file extension */
static void change_ext(char *dst, int dstlen, const char *src, const char *new_ext)
{
    strncpy(dst, src, dstlen - 1);
    dst[dstlen - 1] = '\0';

    /* Find last dot */
    char *dot = strrchr(dst, '.');
    if (dot) *dot = '\0';

    int  base_len = (int)strlen(dst);
    int  ext_len  = (int)strlen(new_ext);
    if (base_len + ext_len + 1 < dstlen) {
        strcat(dst, new_ext);
    }
}

/* Check if file ends with extension (case-insensitive) */
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

int main(int argc, char *argv[])
{
    /* Check if output is a terminal (for color support) */
    use_color = 1; /* default on; disable if not a tty */

    print_header();

    if (argc < 2) {
        print_usage();
        return 1;
    }

    /* Parse arguments */
    const char *input_file = NULL;
    int   output_agiasm   = 0;

    parse_output_flags(argc, argv, &output_agiasm);

    for (int i = 1; i < argc; i++) {
        if (argv[i] && strcmp(argv[i], "--no-color") == 0) use_color = 0;
    }

    for (int i = 1; i < argc; i++) {
        if (!argv[i]) continue;
        if (argv[i][0] == '-') continue;
        if (output_name_is_agiasm(argv[i])) continue;
        input_file = argv[i];
    }

    if (!input_file) {
        print_error("No input file specified.");
        print_usage();
        return 1;
    }

    /* Warn if extension is not .agi */
    if (!has_ext(input_file, ".agi")) {
        char warn[512];
        snprintf(warn, sizeof(warn),
                 "File '%s' does not have .agi extension.", input_file);
        print_warning(warn);
    }

    /* Read and compile */
    {
        char info_msg[512];
        snprintf(info_msg, sizeof(info_msg), "Reading source: %s", input_file);
        print_info(info_msg);
    }

    print_info("Compiling...");

    /* Use static storage to avoid stack overflow (CompileResult is ~142KB) */
    static CompileResult result;
    if (!magic_compile_file(input_file, &result)) {
        char err_msg[512];
        snprintf(err_msg, sizeof(err_msg),
                 "Compilation failed: %.450s", result.error);
        print_error(err_msg);
        return 1;
    }

    if (!result.success) {
        char err_msg[512];
        snprintf(err_msg, sizeof(err_msg),
                 "Compilation error: %.450s", result.error);
        print_error(err_msg);
        return 1;
    }

    /* Determine output file */
    char output_file[1024];
    const char *ext = output_agiasm ? ".agiasm" : ".agic";
    change_ext(output_file, sizeof(output_file), input_file, ext);

    {
        char info_msg[512];
        snprintf(info_msg, sizeof(info_msg), "Saving output: %s", output_file);
        print_info(info_msg);
    }

    /* Save output */
    int save_ok;
    if (output_agiasm) {
        save_ok = magic_save_agiasm(&result.unit, output_file);
    } else {
        save_ok = magic_save_agic(&result.unit, output_file);
    }

    if (!save_ok) {
        char err_msg[512];
        snprintf(err_msg, sizeof(err_msg),
                 "Failed to write output file: %s", output_file);
        print_error(err_msg);
        return 1;
    }

    /* Free allocated blocks */
    for (int i = 0; i < result.unit.proc_count; i++)
        block_free(&result.unit.procedures[i].body);
    for (int i = 0; i < result.unit.func_count; i++)
        block_free(&result.unit.functions[i].body);
    block_free(&result.unit.entry_point);

    print_success("Compilation successful!");
    print_sep();
    printf("%s  Input:  %s%s\n",
           use_color ? COLOR_CYAN : "", input_file,
           use_color ? COLOR_RESET : "");
    printf("%s  Output: %s%s\n",
           use_color ? COLOR_CYAN : "", output_file,
           use_color ? COLOR_RESET : "");
    print_sep();
    printf("\n");

    return 0;
}
