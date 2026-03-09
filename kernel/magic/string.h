/*
 * kernel/magic/string.h - Proxy string.h for kernel-internal Magic build
 *
 * String functions (memcpy, memset, strlen, strcpy, strcmp, etc.) are
 * provided by the kernel's own string.h. This proxy forwards to it.
 */

#ifndef _MAGIC_PROXY_STRING_H
#define _MAGIC_PROXY_STRING_H

#include "../include/string.h"

#endif /* _MAGIC_PROXY_STRING_H */
