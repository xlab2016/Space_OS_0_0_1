/*
 * kernel/magic/stdlib.h - Proxy stdlib.h for kernel-internal Magic build
 *
 * The actual stdlib functions (malloc, free, realloc) are provided via
 * macros in magic_stdio_shim.h which must be included before this file.
 * This stub exists solely to satisfy #include <stdlib.h> in magic sources.
 */

#ifndef _MAGIC_PROXY_STDLIB_H
#define _MAGIC_PROXY_STDLIB_H

/* All needed declarations are already in magic_stdio_shim.h */

#endif /* _MAGIC_PROXY_STDLIB_H */
