/*
 * UnixOS - Minimal C Library
 * Fixed-Width Integer Types
 */

#ifndef _LIBC_STDINT_H
#define _LIBC_STDINT_H

/* ===================================================================== */
/* Exact-width signed integer types                                      */
/* ===================================================================== */

typedef signed char        int8_t;
typedef signed short       int16_t;
typedef signed int         int32_t;
typedef signed long long   int64_t;

/* ===================================================================== */
/* Exact-width unsigned integer types                                    */
/* ===================================================================== */

typedef unsigned char        uint8_t;
typedef unsigned short       uint16_t;
typedef unsigned int         uint32_t;
typedef unsigned long long   uint64_t;

/* ===================================================================== */
/* Minimum-width signed integer types                                    */
/* ===================================================================== */

typedef int8_t   int_least8_t;
typedef int16_t  int_least16_t;
typedef int32_t  int_least32_t;
typedef int64_t  int_least64_t;

/* ===================================================================== */
/* Minimum-width unsigned integer types                                  */
/* ===================================================================== */

typedef uint8_t   uint_least8_t;
typedef uint16_t  uint_least16_t;
typedef uint32_t  uint_least32_t;
typedef uint64_t  uint_least64_t;

/* ===================================================================== */
/* Fastest minimum-width integer types                                   */
/* ===================================================================== */

typedef int8_t   int_fast8_t;
typedef int32_t  int_fast16_t;
typedef int32_t  int_fast32_t;
typedef int64_t  int_fast64_t;

typedef uint8_t   uint_fast8_t;
typedef uint32_t  uint_fast16_t;
typedef uint32_t  uint_fast32_t;
typedef uint64_t  uint_fast64_t;

/* ===================================================================== */
/* Integer types capable of holding a pointer                            */
/* ===================================================================== */

typedef long           intptr_t;
typedef unsigned long  uintptr_t;

/* ===================================================================== */
/* Greatest-width integer types                                          */
/* ===================================================================== */

typedef long long           intmax_t;
typedef unsigned long long  uintmax_t;

/* ===================================================================== */
/* Limits of exact-width signed types                                    */
/* ===================================================================== */

#define INT8_MIN    (-128)
#define INT8_MAX    (127)

#define INT16_MIN   (-32768)
#define INT16_MAX   (32767)

#define INT32_MIN   (-2147483648)
#define INT32_MAX   (2147483647)

#define INT64_MIN   (-9223372036854775807LL - 1)
#define INT64_MAX   (9223372036854775807LL)

/* ===================================================================== */
/* Limits of exact-width unsigned types                                  */
/* ===================================================================== */

#define UINT8_MAX   (255U)
#define UINT16_MAX  (65535U)
#define UINT32_MAX  (4294967295U)
#define UINT64_MAX  (18446744073709551615ULL)

/* ===================================================================== */
/* Limits of minimum-width signed types                                  */
/* ===================================================================== */

#define INT_LEAST8_MIN    INT8_MIN
#define INT_LEAST8_MAX    INT8_MAX
#define INT_LEAST16_MIN   INT16_MIN
#define INT_LEAST16_MAX   INT16_MAX
#define INT_LEAST32_MIN   INT32_MIN
#define INT_LEAST32_MAX   INT32_MAX
#define INT_LEAST64_MIN   INT64_MIN
#define INT_LEAST64_MAX   INT64_MAX

/* ===================================================================== */
/* Limits of minimum-width unsigned types                                */
/* ===================================================================== */

#define UINT_LEAST8_MAX   UINT8_MAX
#define UINT_LEAST16_MAX  UINT16_MAX
#define UINT_LEAST32_MAX  UINT32_MAX
#define UINT_LEAST64_MAX  UINT64_MAX

/* ===================================================================== */
/* Limits of fastest minimum-width signed types                          */
/* ===================================================================== */

#define INT_FAST8_MIN    INT8_MIN
#define INT_FAST8_MAX    INT8_MAX
#define INT_FAST16_MIN   INT32_MIN
#define INT_FAST16_MAX   INT32_MAX
#define INT_FAST32_MIN   INT32_MIN
#define INT_FAST32_MAX   INT32_MAX
#define INT_FAST64_MIN   INT64_MIN
#define INT_FAST64_MAX   INT64_MAX

/* ===================================================================== */
/* Limits of fastest minimum-width unsigned types                        */
/* ===================================================================== */

#define UINT_FAST8_MAX   UINT8_MAX
#define UINT_FAST16_MAX  UINT32_MAX
#define UINT_FAST32_MAX  UINT32_MAX
#define UINT_FAST64_MAX  UINT64_MAX

/* ===================================================================== */
/* Limits of integer types capable of holding a pointer                  */
/* ===================================================================== */

#define INTPTR_MIN   (-9223372036854775807LL - 1)
#define INTPTR_MAX   (9223372036854775807LL)
#define UINTPTR_MAX  (18446744073709551615ULL)

/* ===================================================================== */
/* Limits of greatest-width integer types                                */
/* ===================================================================== */

#define INTMAX_MIN   INT64_MIN
#define INTMAX_MAX   INT64_MAX
#define UINTMAX_MAX  UINT64_MAX

/* ===================================================================== */
/* Macros for integer constants                                          */
/* ===================================================================== */

#define INT8_C(c)    (c)
#define INT16_C(c)   (c)
#define INT32_C(c)   (c)
#define INT64_C(c)   (c ## LL)

#define UINT8_C(c)   (c ## U)
#define UINT16_C(c)  (c ## U)
#define UINT32_C(c)  (c ## U)
#define UINT64_C(c)  (c ## ULL)

#define INTMAX_C(c)  (c ## LL)
#define UINTMAX_C(c) (c ## ULL)

#endif /* _LIBC_STDINT_H */
