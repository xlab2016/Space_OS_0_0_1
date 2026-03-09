/*
 * kernel/magic/ctype.h - Proxy ctype.h for kernel-internal Magic build
 *
 * The actual ctype functions (tolower, isdigit, etc.) are provided as inline
 * functions and macros in magic_stdio_shim.h which must be included before
 * this file. This stub exists solely to satisfy #include <ctype.h> in
 * magic sources.
 */

#ifndef _MAGIC_PROXY_CTYPE_H
#define _MAGIC_PROXY_CTYPE_H

/* All needed declarations are already in magic_stdio_shim.h */

#endif /* _MAGIC_PROXY_CTYPE_H */
