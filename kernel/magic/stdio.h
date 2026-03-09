/*
 * kernel/magic/stdio.h - Proxy stdio.h for kernel-internal Magic build
 *
 * The actual stdio functions (printf, fprintf, fopen, etc.) are provided via
 * macros/inlines in magic_stdio_shim.h which must be included before this
 * file. This stub exists solely to satisfy #include <stdio.h> in magic sources.
 */

#ifndef _MAGIC_PROXY_STDIO_H
#define _MAGIC_PROXY_STDIO_H

/* All needed declarations are already in magic_stdio_shim.h */

#endif /* _MAGIC_PROXY_STDIO_H */
