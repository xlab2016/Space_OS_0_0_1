/*
 * kernel/magic/stdint.h - Proxy stdint.h for kernel-internal Magic build
 *
 * Redirects <stdint.h> includes (from magic.h) to the kernel's own types.h
 * which defines all the fixed-width integer types.
 */

#ifndef _MAGIC_PROXY_STDINT_H
#define _MAGIC_PROXY_STDINT_H

#include "../include/types.h"

#endif /* _MAGIC_PROXY_STDINT_H */
