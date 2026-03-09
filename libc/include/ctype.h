/*
 * UnixOS - Minimal C Library
 * Character classification and case conversion
 */

#ifndef _LIBC_CTYPE_H
#define _LIBC_CTYPE_H

int isalpha(int c);
int isdigit(int c);
int isalnum(int c);
int isspace(int c);
int isupper(int c);
int islower(int c);
int isxdigit(int c);
int isprint(int c);
int ispunct(int c);
int iscntrl(int c);
int isgraph(int c);
int toupper(int c);
int tolower(int c);

#endif /* _LIBC_CTYPE_H */

