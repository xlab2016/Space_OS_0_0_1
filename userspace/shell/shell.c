/*
 * UnixOS - Basic Shell
 *
 * A minimal command interpreter for early testing.
 * Supports Magic language tools: spc (compiler) and spe (emulator).
 */

/* Syscall numbers (AArch64 Linux) */
#define SYS_read        63
#define SYS_write       64
#define SYS_exit        93
#define SYS_getpid      172
#define SYS_uname       160
#define SYS_sched_yield 124
#define SYS_execve      221
#define SYS_fork        1079  /* clone with flags=SIGCHLD */
#define SYS_clone       220
#define SYS_wait4       260

/* Syscall wrappers */
static inline long syscall0(long nr)
{
    register long x0 __asm__("x0");
    register long x8 __asm__("x8") = nr;
    __asm__ volatile("svc #0" : "=r" (x0) : "r" (x8) : "memory");
    return x0;
}

static inline long syscall1(long nr, long a0)
{
    register long x0 __asm__("x0") = a0;
    register long x8 __asm__("x8") = nr;
    __asm__ volatile("svc #0" : "+r" (x0) : "r" (x8) : "memory");
    return x0;
}

static inline long syscall3(long nr, long a0, long a1, long a2)
{
    register long x0 __asm__("x0") = a0;
    register long x1 __asm__("x1") = a1;
    register long x2 __asm__("x2") = a2;
    register long x8 __asm__("x8") = nr;
    __asm__ volatile("svc #0" : "+r" (x0) : "r" (x1), "r" (x2), "r" (x8) : "memory");
    return x0;
}

static inline long syscall4(long nr, long a0, long a1, long a2, long a3)
{
    register long x0 __asm__("x0") = a0;
    register long x1 __asm__("x1") = a1;
    register long x2 __asm__("x2") = a2;
    register long x3 __asm__("x3") = a3;
    register long x8 __asm__("x8") = nr;
    __asm__ volatile("svc #0" : "+r" (x0) : "r" (x1), "r" (x2), "r" (x3), "r" (x8) : "memory");
    return x0;
}

/* String functions */
static int strlen(const char *s)
{
    int len = 0;
    while (*s++) len++;
    return len;
}

static int strcmp(const char *a, const char *b)
{
    while (*a && *b && *a == *b) {
        a++;
        b++;
    }
    return *a - *b;
}

static int strncmp(const char *a, const char *b, int n)
{
    while (n > 0 && *a && *b && *a == *b) {
        a++;
        b++;
        n--;
    }
    if (n == 0) return 0;
    return *a - *b;
}

/* I/O functions */
static void print(const char *msg)
{
    syscall3(SYS_write, 1, (long)msg, strlen(msg));
}

static void putchar(char c)
{
    syscall3(SYS_write, 1, (long)&c, 1);
}

static char getchar(void)
{
    char c;
    syscall3(SYS_read, 0, (long)&c, 1);
    return c;
}

static void print_num(long n)
{
    char buf[32];
    int i = 30;
    int neg = 0;
    
    if (n < 0) {
        neg = 1;
        n = -n;
    }
    
    if (n == 0) {
        buf[i--] = '0';
    }
    
    while (n > 0 && i >= 0) {
        buf[i--] = '0' + (n % 10);
        n /= 10;
    }
    
    if (neg && i >= 0) {
        buf[i--] = '-';
    }
    
    syscall3(SYS_write, 1, (long)&buf[i + 1], 30 - i);
}

/* Command buffer */
#define CMD_MAX 256
static char cmd_buf[CMD_MAX];
static int cmd_len;

/* Read a line of input */
static void readline(void)
{
    cmd_len = 0;
    
    while (cmd_len < CMD_MAX - 1) {
        char c = getchar();
        
        if (c == '\n' || c == '\r') {
            putchar('\n');
            break;
        }
        
        if (c == '\b' || c == 127) {  /* Backspace */
            if (cmd_len > 0) {
                cmd_len--;
                print("\b \b");
            }
            continue;
        }
        
        if (c >= 32 && c < 127) {  /* Printable */
            cmd_buf[cmd_len++] = c;
            putchar(c);
        }
    }
    
    cmd_buf[cmd_len] = '\0';
}

/* Execute an external program with arguments.
 * Uses fork (clone) + execve to run the given path.
 * argv must be null-terminated array of pointers.
 * Returns the exit status of the child, or -1 on error. */
static long exec_program(const char *path, const char *argv[])
{
    /* Use clone(SIGCHLD) to fork */
    long child = syscall4(SYS_clone, 17 /* SIGCHLD */, 0, 0, 0);
    if (child < 0) {
        /* Fork failed - try to exec directly (replaces shell) */
        syscall3(SYS_execve, (long)path, (long)argv, 0);
        return -1;
    }
    if (child == 0) {
        /* Child process */
        syscall3(SYS_execve, (long)path, (long)argv, 0);
        /* execve failed */
        syscall1(SYS_exit, 127);
        return -1;
    }
    /* Parent: wait for child */
    int status = 0;
    syscall4(SYS_wait4, child, (long)&status, 0, 0);
    return (status >> 8) & 0xff; /* exit code */
}

/* Built-in commands */
static void cmd_help(void)
{
    print("\nSpace OS Shell Commands:\n");
    print("  help     - Show this help\n");
    print("  uname    - Show system information\n");
    print("  whoami   - Show current user\n");
    print("  pid      - Show shell PID\n");
    print("  echo     - Print text\n");
    print("  clear    - Clear screen\n");
    print("  spc      - Magic language compiler (spc <file.agi>)\n");
    print("  spe      - Magic language emulator (spe <file>)\n");
    print("  exit     - Exit shell\n");
    print("\n");
}

/* spc: Space Compiler - compile a .agi Magic source file */
static void cmd_spc(const char *args)
{
    if (!args || !*args) {
        print("Usage: spc <file.agi> [--agiasm]\n");
        print("  Compiles Magic language source to .agic bytecode\n");
        return;
    }

    /* Build argv: { "/bin/spc", arg1, arg2, ..., NULL } */
    static const char *spc_path = "/bin/spc";
    /* Parse args into tokens */
    static char arg_buf[256];
    int alen = 0;
    while (args[alen] && alen < 254) {
        arg_buf[alen] = args[alen];
        alen++;
    }
    arg_buf[alen] = '\0';

    const char *argv[4];
    argv[0] = spc_path;
    argv[1] = arg_buf;
    argv[2] = 0;

    /* Check for --agiasm flag */
    static char arg1_buf[256];
    static char arg2_buf[256];
    const char *p = args;
    int a1len = 0, a2len = 0;
    /* First token */
    while (*p && *p != ' ' && a1len < 254) { arg1_buf[a1len++] = *p++; }
    arg1_buf[a1len] = '\0';
    while (*p == ' ') p++;
    /* Second token (optional flag) */
    while (*p && a2len < 254) { arg2_buf[a2len++] = *p++; }
    arg2_buf[a2len] = '\0';

    argv[0] = spc_path;
    argv[1] = arg1_buf;
    if (a2len > 0) {
        argv[2] = arg2_buf;
        argv[3] = 0;
    } else {
        argv[2] = 0;
    }

    long rc = exec_program(spc_path, argv);
    if (rc == 127) {
        print("spc: command not found at /bin/spc\n");
        print("  (Make sure the OS was built with magic tools enabled)\n");
    }
}

/* spe: Space Emulator - run a Magic program */
static void cmd_spe(const char *args)
{
    if (!args || !*args) {
        print("Usage: spe <file> [--verbose]\n");
        print("  Executes Magic language programs (.agi, .agic, .agiasm)\n");
        return;
    }

    static const char *spe_path = "/bin/spe";

    static char arg1_buf[256];
    static char arg2_buf[256];
    const char *p = args;
    int a1len = 0, a2len = 0;
    while (*p && *p != ' ' && a1len < 254) { arg1_buf[a1len++] = *p++; }
    arg1_buf[a1len] = '\0';
    while (*p == ' ') p++;
    while (*p && a2len < 254) { arg2_buf[a2len++] = *p++; }
    arg2_buf[a2len] = '\0';

    const char *argv[4];
    argv[0] = spe_path;
    argv[1] = arg1_buf;
    if (a2len > 0) {
        argv[2] = arg2_buf;
        argv[3] = 0;
    } else {
        argv[2] = 0;
    }

    long rc = exec_program(spe_path, argv);
    if (rc == 127) {
        print("spe: command not found at /bin/spe\n");
        print("  (Make sure the OS was built with magic tools enabled)\n");
    }
}

static void cmd_uname(void)
{
    struct {
        char sysname[65];
        char nodename[65];
        char release[65];
        char version[65];
        char machine[65];
        char domainname[65];
    } uts;
    
    if (syscall1(SYS_uname, (long)&uts) == 0) {
        print(uts.sysname);
        print(" ");
        print(uts.nodename);
        print(" ");
        print(uts.release);
        print(" ");
        print(uts.version);
        print(" ");
        print(uts.machine);
        print("\n");
    } else {
        print("uname failed\n");
    }
}

static void cmd_pid(void)
{
    long pid = syscall0(SYS_getpid);
    print("PID: ");
    print_num(pid);
    print("\n");
}

static void cmd_whoami(void)
{
    print("root\n");
}

static void cmd_echo(const char *args)
{
    if (args && *args) {
        print(args);
    }
    print("\n");
}

static void cmd_clear(void)
{
    /* ANSI escape sequence to clear screen */
    print("\033[2J\033[H");
}

/* Process command */
static void process_command(void)
{
    if (cmd_len == 0) {
        return;
    }
    
    /* Find first space for arguments */
    char *args = cmd_buf;
    while (*args && *args != ' ') args++;
    if (*args == ' ') {
        *args = '\0';
        args++;
        while (*args == ' ') args++;
    }
    
    if (strcmp(cmd_buf, "help") == 0 || strcmp(cmd_buf, "?") == 0) {
        cmd_help();
    }
    else if (strcmp(cmd_buf, "uname") == 0) {
        cmd_uname();
    }
    else if (strcmp(cmd_buf, "whoami") == 0) {
        cmd_whoami();
    }
    else if (strcmp(cmd_buf, "pid") == 0) {
        cmd_pid();
    }
    else if (strncmp(cmd_buf, "echo", 4) == 0) {
        cmd_echo(args);
    }
    else if (strcmp(cmd_buf, "clear") == 0) {
        cmd_clear();
    }
    else if (strcmp(cmd_buf, "spc") == 0) {
        cmd_spc(args);
    }
    else if (strcmp(cmd_buf, "spe") == 0) {
        cmd_spe(args);
    }
    else if (strcmp(cmd_buf, "exit") == 0) {
        print("Goodbye!\n");
        syscall1(SYS_exit, 0);
    }
    else {
        print("sh: ");
        print(cmd_buf);
        print(": command not found\n");
    }
}

/* Entry point */
void _start(void)
{
    print("\n");
    print("Space OS Shell v0.1\n");
    print("Magic language: spc (compiler), spe (emulator)\n");
    print("Type 'help' for available commands.\n");
    print("\n");

    /* Main shell loop */
    while (1) {
        print("space# ");
        readline();
        process_command();
    }
}
