/*
 * UnixOS - Minimal C Library
 * ctype implementation (ASCII only)
 */

#include "../include/ctype.h"

int isalpha(int c) {
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}

int isdigit(int c) {
    return (c >= '0' && c <= '9');
}

int isalnum(int c) {
    return isalpha(c) || isdigit(c);
}

int isspace(int c) {
    return c == ' '  || c == '\t' ||
           c == '\n' || c == '\r' ||
           c == '\f' || c == '\v';
}

int isupper(int c) {
    return (c >= 'A' && c <= 'Z');
}

int islower(int c) {
    return (c >= 'a' && c <= 'z');
}

int isxdigit(int c) {
    return isdigit(c) ||
           (c >= 'a' && c <= 'f') ||
           (c >= 'A' && c <= 'F');
}

int isprint(int c) {
    return (c >= 0x20 && c <= 0x7e);
}

int ispunct(int c) {
    return isprint(c) && !isalnum(c) && !isspace(c);
}

int iscntrl(int c) {
    return (c >= 0x00 && c <= 0x1f) || c == 0x7f;
}

int isgraph(int c) {
    return isprint(c) && c != ' ';
}

int toupper(int c) {
    if (islower(c))
        return c - ('a' - 'A');
    return c;
}

int tolower(int c) {
    if (isupper(c))
        return c + ('a' - 'A');
    return c;
}

