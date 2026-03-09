/*
 * kernel/magic/stddef.h - Proxy stddef.h for kernel-internal Magic build
 *
 * Redirects <stddef.h> includes (from magic.h) to the kernel's own types.h
 * which defines size_t, ptrdiff_t, NULL, and offsetof.
 */

#ifndef _MAGIC_PROXY_STDDEF_H
#define _MAGIC_PROXY_STDDEF_H

#include "../include/types.h"

#endif /* _MAGIC_PROXY_STDDEF_H */
