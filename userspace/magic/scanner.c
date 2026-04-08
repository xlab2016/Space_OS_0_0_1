/*
 * scanner.c - Magic Language Lexer/Scanner
 *
 * Tokenizes Magic (.agi) source code into a token stream.
 * Faithful C port of Magic.Kernel/Compilation/Scanner.cs
 */

#include "magic.h"
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdio.h>

/* ------------------------------------------------------------------ */
/* Internal helpers                                                    */
/* ------------------------------------------------------------------ */

/* Returns 0 on realloc failure (caller must not write past tok_cap). */
static int ensure_capacity(Scanner *s)
{
    if (s->tok_count < s->tok_cap)
        return 1;
    int new_cap = s->tok_cap == 0 ? 64 : s->tok_cap * 2;
    Token *new_tokens = realloc(s->tokens, (size_t)new_cap * sizeof(Token));
    if (!new_tokens) {
        fprintf(stderr, "[scanner] out of memory (need cap %d)\n", new_cap);
#if defined(MAGIC_KERNEL_DIAG)
        magic_compile_diag_i2("scanner_realloc_fail", s->tok_cap, new_cap);
#endif
        return 0;
    }
    s->tokens = new_tokens;
    s->tok_cap = new_cap;
    return 1;
}

static int add_token(Scanner *s, Token tok)
{
    if (!ensure_capacity(s))
        return 0;
    s->tokens[s->tok_count++] = tok;
    return 1;
}

static Token make_tok(TokenKind kind, const char *val, int start, int end)
{
    Token t;
    t.kind  = kind;
    t.start = start;
    t.end   = end;
    if (val) {
        strncpy(t.value, val, TOKEN_VALUE_MAX - 1);
        t.value[TOKEN_VALUE_MAX - 1] = '\0';
    } else {
        t.value[0] = '\0';
    }
    return t;
}

static Token make_eof(int pos)
{
    return make_tok(TOK_EOF, "", pos, pos);
}

/* ------------------------------------------------------------------ */
/* Skip whitespace (spaces and tabs only; newlines become tokens)      */
/* ------------------------------------------------------------------ */
static void skip_whitespace(Scanner *s)
{
    while (s->pos < s->src_len &&
           (s->src[s->pos] == ' ' || s->src[s->pos] == '\t'))
        s->pos++;
}

/* ------------------------------------------------------------------ */
/* Skip line comments: // ... \n                                       */
/* ------------------------------------------------------------------ */
static void skip_line_comments(Scanner *s)
{
    while (s->pos + 1 < s->src_len &&
           s->src[s->pos] == '/' && s->src[s->pos + 1] == '/')
    {
        s->pos += 2;
        while (s->pos < s->src_len &&
               s->src[s->pos] != '\n' && s->src[s->pos] != '\r')
            s->pos++;
        if (s->pos < s->src_len) {
            char c = s->src[s->pos++];
            if (c == '\r' && s->pos < s->src_len && s->src[s->pos] == '\n')
                s->pos++;
        }
        skip_whitespace(s);
    }
}

/* ------------------------------------------------------------------ */
/* Try to scan symbolic identifier: ident followed by <>, > (colon)   */
/* Returns 1 on success, sets tok                                      */
/* ------------------------------------------------------------------ */
static int try_scan_symbolic(Scanner *s, Token *tok)
{
    int i = s->pos;
    if (i >= s->src_len || !(isalpha(s->src[i]) || s->src[i] == '_'))
        return 0;
    i++;
    while (i < s->src_len && (isalnum(s->src[i]) || s->src[i] == '_'))
        i++;

    int consumed = 0;
    while (i < s->src_len) {
        /* "<>" suffix e.g. Messages<> */
        if (s->src[i] == '<' && i + 1 < s->src_len && s->src[i + 1] == '>') {
            i += 2;
            consumed = 1;
            continue;
        }
        /* "Db>>" -> consume first '>' as symbolic, leave second */
        if (s->src[i] == '>' && i + 1 < s->src_len && s->src[i + 1] == '>') {
            i++;
            consumed = 1;
            break;
        }
        /* "Db> :" -> consume '>' as symbolic suffix before colon-like boundary */
        if (s->src[i] == '>') {
            /* peek for next non-whitespace */
            int j = i + 1;
            while (j < s->src_len && (s->src[j] == ' ' || s->src[j] == '\t'))
                j++;
            if (j < s->src_len && s->src[j] == ':') {
                i++;
                consumed = 1;
                break;
            }
        }
        break;
    }

    if (!consumed)
        return 0;

    int len = i - s->pos;
    if (len <= 0 || len >= TOKEN_VALUE_MAX)
        return 0;

    char buf[TOKEN_VALUE_MAX];
    memcpy(buf, s->src + s->pos, len);
    buf[len] = '\0';

    *tok = make_tok(TOK_IDENTIFIER, buf, s->pos, i);
    s->pos = i;
    return 1;
}

/* ------------------------------------------------------------------ */
/* Scan one token                                                      */
/* ------------------------------------------------------------------ */
static Token scan_one(Scanner *s)
{
    if (s->pos >= s->src_len)
        return make_eof(s->pos);

    int start = s->pos;
    char ch   = s->src[s->pos];

    /* Newline */
    if (ch == '\r' || ch == '\n') {
        s->pos++;
        if (ch == '\r' && s->pos < s->src_len && s->src[s->pos] == '\n')
            s->pos++;
        return make_tok(TOK_NEWLINE, "\n", start, s->pos);
    }

    /* Symbolic identifier: ident<> or ident> before colon */
    {
        Token sym;
        if (try_scan_symbolic(s, &sym))
            return sym;
    }

    /* Single-character tokens */
    switch (ch) {
        case ':': s->pos++; return make_tok(TOK_COLON, ":", start, s->pos);
        case ',': s->pos++; return make_tok(TOK_COMMA, ",", start, s->pos);
        case '[': s->pos++; return make_tok(TOK_LBRACKET, "[", start, s->pos);
        case ']': s->pos++; return make_tok(TOK_RBRACKET, "]", start, s->pos);
        case '(': s->pos++; return make_tok(TOK_LPAREN, "(", start, s->pos);
        case ')': s->pos++; return make_tok(TOK_RPAREN, ")", start, s->pos);
        case '{': s->pos++; return make_tok(TOK_LBRACE, "{", start, s->pos);
        case '}': s->pos++; return make_tok(TOK_RBRACE, "}", start, s->pos);
        case '.': s->pos++; return make_tok(TOK_DOT, ".", start, s->pos);
        case '=': s->pos++; return make_tok(TOK_ASSIGN, "=", start, s->pos);
        case '<': s->pos++; return make_tok(TOK_LESS, "<", start, s->pos);
        case '>': s->pos++; return make_tok(TOK_GREATER, ">", start, s->pos);
        case ';': s->pos++; return make_tok(TOK_SEMICOLON, ";", start, s->pos);
        default:  break;
    }

    /* String literal: "..." */
    if (ch == '"') {
        s->pos++;
        int value_start = s->pos;
        char buf[TOKEN_VALUE_MAX];
        int  blen = 0;
        while (s->pos < s->src_len && s->src[s->pos] != '"') {
            if (s->src[s->pos] == '\\' && s->pos + 1 < s->src_len) {
                char next = s->src[s->pos + 1];
                if (next == '"') {
                    if (blen < TOKEN_VALUE_MAX - 1) buf[blen++] = '"';
                    s->pos += 2;
                } else if (next == 'n') {
                    if (blen < TOKEN_VALUE_MAX - 1) buf[blen++] = '\n';
                    s->pos += 2;
                } else if (next == 't') {
                    if (blen < TOKEN_VALUE_MAX - 1) buf[blen++] = '\t';
                    s->pos += 2;
                } else if (next == '\\') {
                    if (blen < TOKEN_VALUE_MAX - 1) buf[blen++] = '\\';
                    s->pos += 2;
                } else {
                    if (blen < TOKEN_VALUE_MAX - 1) buf[blen++] = s->src[s->pos];
                    s->pos++;
                }
            } else {
                if (blen < TOKEN_VALUE_MAX - 1) buf[blen++] = s->src[s->pos];
                s->pos++;
            }
        }
        buf[blen] = '\0';
        if (s->pos < s->src_len) s->pos++; /* closing " */
        (void)value_start;
        return make_tok(TOK_STRING, buf, start, s->pos);
    }

    /* Number (including negative) */
    if (ch == '-' || isdigit(ch)) {
        int i = s->pos;
        int has_dot = 0, has_exp = 0;
        if (ch == '-') i++;
        while (i < s->src_len &&
               (isdigit(s->src[i]) || s->src[i] == '.' ||
                s->src[i] == 'e' || s->src[i] == 'E' ||
                s->src[i] == '+'))
        {
            if (s->src[i] == '.') has_dot = 1;
            if (s->src[i] == 'e' || s->src[i] == 'E') has_exp = 1;
            i++;
        }
        int len = i - s->pos;
        if (len <= 0 || len >= TOKEN_VALUE_MAX) {
            /* Not a valid number; fall through to identifier */
            goto try_ident;
        }
        char buf[TOKEN_VALUE_MAX];
        memcpy(buf, s->src + s->pos, len);
        buf[len] = '\0';
        s->pos = i;
        TokenKind k = (has_dot || has_exp) ? TOK_FLOAT : TOK_NUMBER;
        return make_tok(k, buf, start, s->pos);
    }

try_ident:
    /* Identifier */
    if (isalpha(ch) || ch == '_') {
        int i = s->pos + 1;
        while (i < s->src_len && (isalnum(s->src[i]) || s->src[i] == '_'))
            i++;
        int len = i - s->pos;
        if (len >= TOKEN_VALUE_MAX) len = TOKEN_VALUE_MAX - 1;
        char buf[TOKEN_VALUE_MAX];
        memcpy(buf, s->src + s->pos, len);
        buf[len] = '\0';
        s->pos = i;
        return make_tok(TOK_IDENTIFIER, buf, start, s->pos);
    }

    /* Unknown character — return as single-char identifier */
    {
        char buf[2] = { ch, '\0' };
        s->pos++;
        return make_tok(TOK_IDENTIFIER, buf, start, s->pos);
    }
}

/* ------------------------------------------------------------------ */
/* Scan all tokens upfront                                             */
/* ------------------------------------------------------------------ */
static void scan_all(Scanner *s)
{
    s->tok_count = 0;
    s->tok_pos   = 0;
    s->pos       = 0;

#if defined(MAGIC_KERNEL_DIAG)
    magic_compile_diag_i2("scan_all_begin", s->src_len, 0);
#endif

    skip_whitespace(s);
    while (s->pos < s->src_len) {
        skip_line_comments(s);
        if (s->pos >= s->src_len)
            break;
        Token tok = scan_one(s);
        if (!add_token(s, tok)) {
#if defined(MAGIC_KERNEL_DIAG)
            magic_compile_diag("scan_all aborted (OOM)");
#endif
            break;
        }
#if defined(MAGIC_KERNEL_DIAG)
        if ((s->tok_count & 15) == 1)
            magic_compile_diag_i2("scan token", s->tok_count, (int)tok.kind);
#endif
        if (tok.kind == TOK_EOF)
            break;
        skip_whitespace(s);
    }
    if (!add_token(s, make_eof(s->pos))) {
#if defined(MAGIC_KERNEL_DIAG)
        magic_compile_diag("scan_all: failed to append EOF token");
#endif
    }

#if defined(MAGIC_KERNEL_DIAG)
    magic_compile_diag_i2("scan_all_done", s->tok_count, s->tok_cap);
#endif
}

/* ------------------------------------------------------------------ */
/* Public API                                                          */
/* ------------------------------------------------------------------ */

int scanner_init(Scanner *s, const char *src)
{
    memset(s, 0, sizeof(*s));
    s->src     = src ? src : "";
    s->src_len = (int)strlen(s->src);
    s->tokens  = NULL;
    s->tok_cap = 0;
    scan_all(s);
    return 1;
}

void scanner_free(Scanner *s)
{
    if (s->tokens)
        free(s->tokens);
    s->tokens    = NULL;
    s->tok_count = 0;
    s->tok_cap   = 0;
}

Token scanner_current(Scanner *s)
{
    if (s->tok_pos < s->tok_count)
        return s->tokens[s->tok_pos];
    return make_eof(s->pos);
}

Token scanner_scan(Scanner *s)
{
    if (s->tok_pos >= s->tok_count)
        return make_eof(s->pos);
    return s->tokens[s->tok_pos++];
}

Token *scanner_watch(Scanner *s, int offset)
{
    int i = s->tok_pos + offset;
    if (i < 0 || i >= s->tok_count)
        return NULL;
    return &s->tokens[i];
}

int scanner_save(Scanner *s)
{
    return s->tok_pos;
}

void scanner_restore(Scanner *s, int pos)
{
    s->tok_pos = pos;
}
