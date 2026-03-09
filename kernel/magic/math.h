/*
 * kernel/magic/math.h - Proxy math.h for kernel-internal Magic build
 *
 * The math function (fabs) is provided as an inline in magic_stdio_shim.h.
 * This stub exists solely to satisfy #include <math.h> in interpreter.c.
 */

#ifndef _MAGIC_PROXY_MATH_H
#define _MAGIC_PROXY_MATH_H

/* fabs is defined as magic_fabs inline in magic_stdio_shim.h */

#endif /* _MAGIC_PROXY_MATH_H */
