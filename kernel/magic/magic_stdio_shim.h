/*
 * magic_stdio_shim.h - Kernel-side stdio/libc shim for Magic language runtime
 *
 * Maps standard libc functions (printf, fopen, malloc, etc.) to kernel
 * equivalents so that Magic compiler/interpreter sources can be compiled
 * directly into the kernel without musl libc.
 *
 * Include this file BEFORE magic.h in any kernel-side translation unit
 * that includes the Magic C sources.
 */

#ifndef MAGIC_STDIO_SHIM_H
#define MAGIC_STDIO_SHIM_H

#include "../include/types.h"
#include "../include/printk.h"
#include "../include/mm/kmalloc.h"
#include "../include/fs/vfs_compat.h"
#include "../include/string.h"
#include "../include/stdarg.h"

#ifndef EOF
#define EOF (-1)
#endif

/* ------------------------------------------------------------------ */
/* Memory allocation: map to kernel allocator                          */
/* ------------------------------------------------------------------ */

#define malloc(sz)          _kmalloc((sz), 0)
#define free(ptr)           kfree(ptr)
#define realloc(ptr, sz)    krealloc((ptr), (sz), 0)

/* ------------------------------------------------------------------ */
/* stdlib.h numeric conversions: atol, atof                           */
/* ------------------------------------------------------------------ */

static inline long magic_atol(const char *s) {
    long result = 0;
    int neg = 0;
    while (*s == ' ' || *s == '\t') s++;
    if (*s == '-') { neg = 1; s++; }
    else if (*s == '+') s++;
    while (*s >= '0' && *s <= '9') {
        result = result * 10 + (*s - '0');
        s++;
    }
    return neg ? -result : result;
}

static inline double magic_atof(const char *s) {
    double result = 0.0;
    double frac = 0.0;
    double div = 1.0;
    int neg = 0;
    int in_frac = 0;
    while (*s == ' ' || *s == '\t') s++;
    if (*s == '-') { neg = 1; s++; }
    else if (*s == '+') s++;
    while (*s >= '0' && *s <= '9') {
        if (!in_frac) result = result * 10.0 + (*s - '0');
        else { frac = frac * 10.0 + (*s - '0'); div *= 10.0; }
        s++;
        if (!in_frac && *s == '.') { in_frac = 1; s++; }
    }
    result += frac / div;
    return neg ? -result : result;
}

#define atol(s)  magic_atol(s)
#define atof(s)  magic_atof(s)

/* ------------------------------------------------------------------ */
/* ctype.h: provide minimal inline versions                            */
/* ------------------------------------------------------------------ */

static inline int magic_tolower(int c) {
    return (c >= 'A' && c <= 'Z') ? (c + 32) : c;
}
static inline int magic_isdigit(int c) { return c >= '0' && c <= '9'; }
static inline int magic_isalpha(int c) {
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
static inline int magic_isalnum(int c) {
    return magic_isalpha(c) || magic_isdigit(c);
}
static inline int magic_isspace(int c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r';
}

#define tolower(c)  magic_tolower(c)
#define isdigit(c)  magic_isdigit(c)
#define isalpha(c)  magic_isalpha(c)
#define isalnum(c)  magic_isalnum(c)
#define isspace(c)  magic_isspace(c)

/* ------------------------------------------------------------------ */
/* snprintf / printf output                                            */
/*                                                                     */
/* Magic output hook: before calling spc/spe, the terminal sets this  */
/* to redirect output to the terminal buffer instead of UART.         */
/* ------------------------------------------------------------------ */

/* Declared and defined in magic_kern.c */
extern void (*magic_output_hook)(const char *buf, size_t len);

static inline void magic_emit(const char *buf) {
    if (magic_output_hook) {
        size_t len = 0;
        while (buf[len]) len++;
        magic_output_hook(buf, len);
    } else {
        printk("%s", buf);
    }
}

/* snprintf: use the kernel's ksnprintf */
#define snprintf ksnprintf

/* printf: format to buffer then emit via hook */
static inline int magic_printf(const char *fmt, ...) __attribute__((format(printf, 1, 2)));
static inline int magic_printf(const char *fmt, ...) {
    char buf[512];
    va_list args;
    va_start(args, fmt);
    int n = kvsnprintf_pub(buf, sizeof(buf), fmt, args);
    va_end(args);
    magic_emit(buf);
    return n;
}
#define printf magic_printf

/* ------------------------------------------------------------------ */
/* FILE* type and stderr                                               */
/* ------------------------------------------------------------------ */

/* Kernel FILE wrapper */
typedef struct magic_kfile {
    /* For reading: in-memory buffer loaded from VFS */
    char   *rbuf;
    size_t  rsize;
    size_t  rpos;
    /* For writing: growing in-memory buffer */
    char   *wbuf;
    size_t  wbuf_cap;
    size_t  wbuf_len;
    /* 1=write buffer, 0=read buffer, -1=stderr, -2=stdout (not "is writing") */
    int     writing;
    /* Saved path for writing (so we can flush to VFS on fclose) */
    char    path[256];
} MAGIC_KFILE;

#define MAGIC_KF_READ  0
#define MAGIC_KF_WRITE 1
#define MAGIC_KF_ERR   (-1)
#define MAGIC_KF_OUT   (-2)

/* stderr/stdout: negative .writing so they never match read-mode (0) */
static MAGIC_KFILE magic_stderr_obj = {.writing = MAGIC_KF_ERR};
static MAGIC_KFILE magic_stdout_obj = {.writing = MAGIC_KF_OUT};

static inline MAGIC_KFILE *magic_get_stderr(void) {
    return &magic_stderr_obj;
}

#undef  FILE
#undef  stderr
#define FILE    MAGIC_KFILE
#define stderr  (magic_get_stderr())

/* fprintf: stderr output goes to printk; file output goes to write buffer */
static inline int magic_fprintf_stderr(const char *fmt, ...) __attribute__((format(printf, 1, 2)));
static inline int magic_fprintf_stderr(const char *fmt, ...) {
    char buf[512];
    va_list args;
    va_start(args, fmt);
    int n = kvsnprintf_pub(buf, sizeof(buf), fmt, args);
    va_end(args);
    printk("%s", buf);
    return n;
}

static inline int magic_fprintf_file(MAGIC_KFILE *f, const char *fmt, ...) __attribute__((format(printf, 2, 3)));
static inline int magic_fprintf_file(MAGIC_KFILE *f, const char *fmt, ...) {
    if (!f) return 0;
    char buf[1024];
    va_list args;
    va_start(args, fmt);
    int n = kvsnprintf_pub(buf, sizeof(buf), fmt, args);
    va_end(args);
    if (n > 0 && f->writing == MAGIC_KF_WRITE) {
        while (f->wbuf_len + (size_t)n > f->wbuf_cap) {
            size_t new_cap = f->wbuf_cap ? f->wbuf_cap * 2 : 4096;
            char *nb = (char *)krealloc(f->wbuf, new_cap, 0);
            if (!nb) return 0;
            f->wbuf = nb;
            f->wbuf_cap = new_cap;
        }
        for (int i = 0; i < n; i++) f->wbuf[f->wbuf_len++] = buf[i];
    }
    return n;
}

#undef  fprintf
#define fprintf(f, ...)                                                        \
    ((((MAGIC_KFILE *)(f))->writing == MAGIC_KF_ERR) ||                        \
     (((MAGIC_KFILE *)(f))->writing == MAGIC_KF_OUT))                          \
        ? magic_fprintf_stderr(__VA_ARGS__)                                    \
        : magic_fprintf_file((MAGIC_KFILE *)(f), __VA_ARGS__)

/* ------------------------------------------------------------------ */
/* FILE* operations                                                    */
/* ------------------------------------------------------------------ */

#define SEEK_SET 0
#define SEEK_CUR 1
#define SEEK_END 2

static inline FILE *magic_fopen(const char *path, const char *mode) {
    MAGIC_KFILE *f = (MAGIC_KFILE *)_kmalloc(sizeof(MAGIC_KFILE), 0);
    if (!f) return NULL;
    {
        unsigned char *p = (unsigned char *)f;
        for (size_t i = 0; i < sizeof(MAGIC_KFILE); i++) p[i] = 0;
    }

    int want_write = (mode[0] == 'w');
    f->writing = want_write ? MAGIC_KF_WRITE : MAGIC_KF_READ;

    size_t plen = 0;
    while (path[plen] && plen < 255) plen++;
    for (size_t i = 0; i < plen; i++) f->path[i] = path[i];
    f->path[plen] = '\0';

    if (!want_write) {
        vfs_node_t *node = vfs_lookup(path);
        if (!node) { kfree(f); return NULL; }
        f->rsize = node->size;
        f->rbuf  = (char *)_kmalloc(f->rsize + 1, 0);
        if (!f->rbuf) { kfree(f); return NULL; }
        int bytes = vfs_read_compat(node, f->rbuf, f->rsize, 0);
        if (bytes < 0) bytes = 0;
        f->rbuf[bytes] = '\0';
        f->rsize = (size_t)bytes;
        f->rpos  = 0;
    } else {
        f->wbuf_cap = 4096;
        f->wbuf     = (char *)_kmalloc(f->wbuf_cap, 0);
        if (!f->wbuf) { kfree(f); return NULL; }
        f->wbuf_len = 0;
    }
    return f;
}

static inline int magic_fseek(FILE *f, long offset, int whence) {
    if (!f || f->writing != MAGIC_KF_READ) return -1;
    size_t new_pos;
    if (whence == SEEK_SET)
        new_pos = (offset >= 0) ? (size_t)offset : 0;
    else if (whence == SEEK_END)
        new_pos = (offset >= 0)
                    ? f->rsize + (size_t)offset
                    : (f->rsize >= (size_t)(-offset) ? f->rsize - (size_t)(-offset) : 0);
    else /* SEEK_CUR */
        new_pos = (offset >= 0)
                    ? f->rpos + (size_t)offset
                    : (f->rpos >= (size_t)(-offset) ? f->rpos - (size_t)(-offset) : 0);
    if (new_pos > f->rsize) new_pos = f->rsize;
    f->rpos = new_pos;
    return 0;
}

static inline long magic_ftell(FILE *f) {
    if (!f || f->writing != MAGIC_KF_READ) return -1L;
    return (long)f->rpos;
}

static inline size_t magic_fread(void *ptr, size_t size, size_t nmemb, FILE *f) {
    if (!f || f->writing != MAGIC_KF_READ || !f->rbuf) return 0;
    size_t avail  = f->rsize - f->rpos;
    size_t want   = size * nmemb;
    if (want > avail) want = avail;
    size_t count  = (size > 0) ? want / size : 0;
    size_t actual = count * size;
    char *dst = (char *)ptr;
    char *src = f->rbuf + f->rpos;
    for (size_t i = 0; i < actual; i++) dst[i] = src[i];
    f->rpos += actual;
    return count;
}

static inline size_t magic_fwrite(const void *ptr, size_t size, size_t nmemb, FILE *f) {
    if (!f || f->writing != MAGIC_KF_WRITE) return 0;
    size_t total = size * nmemb;
    while (f->wbuf_len + total > f->wbuf_cap) {
        size_t new_cap = f->wbuf_cap ? f->wbuf_cap * 2 : 4096;
        char *nb = (char *)krealloc(f->wbuf, new_cap, 0);
        if (!nb) return 0;
        f->wbuf = nb;
        f->wbuf_cap = new_cap;
    }
    const char *src = (const char *)ptr;
    for (size_t i = 0; i < total; i++) f->wbuf[f->wbuf_len + i] = src[i];
    f->wbuf_len += total;
    return nmemb;
}

static inline int magic_fputc(int c, FILE *f) {
    unsigned char ch = (unsigned char)c;
    return (magic_fwrite(&ch, 1, 1, f) == 1) ? c : -1;
}

static inline int magic_fputs(const char *s, FILE *f) {
    size_t len = 0;
    while (s[len]) len++;
    return (int)magic_fwrite(s, 1, len, f);
}

static inline int magic_fclose(FILE *f) {
    if (!f)
        return EOF;
    /* Static stderr/stdout — never free */
    if (f->writing == MAGIC_KF_ERR || f->writing == MAGIC_KF_OUT)
        return 0;

    int ok = 1;
    if (f->writing == MAGIC_KF_WRITE) {
        printk(KERN_INFO
               "[spc-diag] magic_fclose: path=\"%s\" wbuf_len=%lu writing=%d\n",
               f->path, (unsigned long)f->wbuf_len, f->writing);
        if (f->wbuf_len > 0 && f->wbuf_len <= 48) {
            char preview[160];
            int pi = 0;
            for (size_t i = 0; i < f->wbuf_len && pi < (int)sizeof(preview) - 4;
                 i++) {
                unsigned char c = (unsigned char)f->wbuf[i];
                if (c >= 32 && c < 127)
                    preview[pi++] = (char)c;
                else {
                    preview[pi++] = '\\';
                    preview[pi++] = 'x';
                    preview[pi++] = "0123456789abcdef"[c >> 4];
                    preview[pi++] = "0123456789abcdef"[c & 15];
                }
            }
            preview[pi] = '\0';
            printk(KERN_INFO "[spc-diag] magic_fclose: preview \"%s\"\n", preview);
        } else if (f->wbuf_len == 0) {
            printk(KERN_WARNING
                   "[spc-diag] magic_fclose: wbuf_len=0 (nothing buffered — "
                   "fprintf may have routed to stderr or format failed)\n");
        }
        extern int ramfs_write_bytes_at_path(const char *path, const uint8_t *data,
                                             size_t len);
        if (f->path[0] != '/') {
            printk(KERN_ERR "[spc-diag] magic_fclose: need absolute path, got '%s'\n",
                   f->path);
            ok = 0;
        } else {
            int rw = ramfs_write_bytes_at_path(f->path, (const uint8_t *)f->wbuf,
                                                 f->wbuf_len);
            if (rw != 0) {
                printk(KERN_ERR
                       "[spc-diag] magic_fclose: ramfs_write_bytes_at_path(\"%s\") "
                       "rc=%d len=%lu\n",
                       f->path, rw, (unsigned long)f->wbuf_len);
                ok = 0;
            } else {
                printk(KERN_INFO
                       "[spc-diag] magic_fclose: wrote %lu bytes to \"%s\"\n",
                       (unsigned long)f->wbuf_len, f->path);
            }
        }
    }
    if (f->rbuf) kfree(f->rbuf);
    if (f->wbuf) kfree(f->wbuf);
    kfree(f);
    return ok ? 0 : EOF;
}

#define fopen(path, mode)      magic_fopen((path), (mode))
#define fclose(f)              magic_fclose((FILE *)(f))
#define fread(ptr, sz, n, f)   magic_fread((ptr), (sz), (n), (FILE *)(f))
#define fwrite(ptr, sz, n, f)  magic_fwrite((ptr), (sz), (n), (FILE *)(f))
#define fseek(f, off, wh)      magic_fseek((FILE *)(f), (off), (wh))
#define ftell(f)               magic_ftell((FILE *)(f))
#define fputc(c, f)            magic_fputc((c), (FILE *)(f))
#define fputs(s, f)            magic_fputs((s), (FILE *)(f))

/* fgetc: read one byte from a read-mode MAGIC_KFILE */
static inline int magic_fgetc(FILE *f) {
    if (!f || f->writing != MAGIC_KF_READ || !f->rbuf || f->rpos >= f->rsize)
        return EOF;
    return (unsigned char)f->rbuf[f->rpos++];
}
#define fgetc(f) magic_fgetc((FILE *)(f))

static inline MAGIC_KFILE *magic_get_stdout(void) { return &magic_stdout_obj; }
#define stdout (magic_get_stdout())

/* fflush: no-op in kernel context (output is synchronous) */
#define fflush(f) ((void)(f), 0)

/* ------------------------------------------------------------------ */
/* math.h stub (interpreter.c includes <math.h>)                      */
/* ------------------------------------------------------------------ */

static inline double magic_fabs(double x) { return x < 0.0 ? -x : x; }
#define fabs magic_fabs

#endif /* MAGIC_STDIO_SHIM_H */
