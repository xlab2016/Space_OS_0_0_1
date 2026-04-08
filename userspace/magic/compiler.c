/*
 * compiler.c - Magic Language Compiler (Parser + Semantic Analyzer)
 *
 * Parses Magic (.agi) source code into an ExecutableUnit.
 * Faithful C port of:
 *   Magic.Kernel/Compilation/Parser.cs
 *   Magic.Kernel/Compilation/InstructionParser.cs
 *   Magic.Kernel/Compilation/SemanticAnalyzer.cs
 *   Magic.Kernel/Compilation/StatementLoweringCompiler.cs
 */

#include "magic.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>

/* ------------------------------------------------------------------ */
/* String helpers                                                      */
/* ------------------------------------------------------------------ */

static void str_lower(char *dst, const char *src, int dstlen)
{
    int i = 0;
    while (src[i] && i < dstlen - 1) {
        dst[i] = (char)tolower((unsigned char)src[i]);
        i++;
    }
    dst[i] = '\0';
}

static int str_eq_ci(const char *a, const char *b)
{
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b))
            return 0;
        a++; b++;
    }
    return *a == *b;
}

/* ------------------------------------------------------------------ */
/* Block helpers                                                       */
/* ------------------------------------------------------------------ */

void block_init(Block *b)
{
    b->instrs = NULL;
    b->count  = 0;
    b->cap    = 0;
}

void block_free(Block *b)
{
    if (b->instrs)
        free(b->instrs);
    b->instrs = NULL;
    b->count  = b->cap = 0;
}

int block_add(Block *b, Instruction instr)
{
    if (b->count >= b->cap) {
        int new_cap = b->cap == 0 ? BLOCK_INIT_CAP : b->cap * 2;
        Instruction *ni = realloc(b->instrs, new_cap * sizeof(Instruction));
        if (!ni) return 0;
        b->instrs = ni;
        b->cap    = new_cap;
    }
    b->instrs[b->count++] = instr;
    return 1;
}

/* ------------------------------------------------------------------ */
/* Operand builders                                                    */
/* ------------------------------------------------------------------ */

static Operand op_none(void)
{
    Operand o; memset(&o, 0, sizeof(o)); o.kind = OPERAND_NONE; return o;
}
static Operand op_int(long v)
{
    Operand o = op_none(); o.kind = OPERAND_INT; o.int_val = v; return o;
}
static Operand op_str(const char *s)
{
    Operand o = op_none(); o.kind = OPERAND_STRING;
    strncpy(o.str_val, s, OPERAND_STR_MAX - 1); return o;
}
static Operand op_mem(long idx, int is_global)
{
    Operand o = op_none();
    o.kind      = is_global ? OPERAND_GLOBAL_MEMORY : OPERAND_MEMORY;
    o.mem_index = idx;
    o.is_global = is_global;
    return o;
}
static Operand op_label(const char *name)
{
    Operand o = op_none(); o.kind = OPERAND_LABEL;
    strncpy(o.str_val, name, OPERAND_STR_MAX - 1); return o;
}
static Operand op_call(const char *fn)
{
    Operand o = op_none(); o.kind = OPERAND_CALL_INFO;
    strncpy(o.str_val, fn, OPERAND_STR_MAX - 1); return o;
}
static Operand op_type(const char *name)
{
    Operand o = op_none(); o.kind = OPERAND_TYPE;
    strncpy(o.str_val, name, OPERAND_STR_MAX - 1); return o;
}
static Operand op_float(double v)
{
    Operand o = op_none(); o.kind = OPERAND_FLOAT; o.float_val = v; return o;
}

/* ------------------------------------------------------------------ */
/* Instruction builder                                                 */
/* ------------------------------------------------------------------ */

static Instruction make_instr(Opcode op, Operand o1, Operand o2)
{
    Instruction i;
    i.opcode = op;
    i.op1    = o1;
    i.op2    = o2;
    return i;
}

/* ------------------------------------------------------------------ */
/* Parser context                                                      */
/* ------------------------------------------------------------------ */

/* Use directive: use <module_path> [as [<alias> | { function f1; ... }]]; */
#define USE_MAX 32
#define USE_SIG_MAX 32
#define USE_PATH_MAX 512
#define USE_SIG_NAME_MAX 128

typedef struct {
    char name[USE_SIG_NAME_MAX];  /* function or procedure name */
    int  is_function;             /* 1 = function, 0 = procedure */
} UseSignature;

typedef struct {
    char         module_path[USE_PATH_MAX];  /* raw module path, e.g. "modularity\module1" */
    char         alias[USE_SIG_NAME_MAX];    /* alias (last segment of path if unset) */
    UseSignature sigs[USE_SIG_MAX];          /* explicit imports (empty = import all) */
    int          sig_count;                  /* number of explicit signatures */
    int          has_as_block;               /* 1 if "as { ... }" form */
} UseDirective;

typedef struct {
    UseDirective items[USE_MAX];
    int          count;
} UseDirectiveSet;

typedef struct {
    Scanner          scanner;
    CompileResult   *result;
    const char      *source;
    UseDirectiveSet *uses;  /* if non-NULL, collect use directives */
} Parser;

static void set_error(Parser *p, const char *msg, int pos)
{
    p->result->success = 0;
    strncpy(p->result->error, msg, sizeof(p->result->error) - 1);
    if (pos >= 0) {
        int line, col;
        magic_get_line_col(p->source, pos, &line, &col);
        p->result->error_line = line;
        p->result->error_col  = col;
        snprintf(p->result->error + strlen(p->result->error),
                 sizeof(p->result->error) - strlen(p->result->error) - 1,
                 " (line %d, col %d)", line, col);
    }
}

static void skip_newlines(Parser *p)
{
    while (scanner_current(&p->scanner).kind == TOK_NEWLINE)
        scanner_scan(&p->scanner);
}

static Token expect_token(Parser *p, TokenKind kind)
{
    Token t = scanner_scan(&p->scanner);
    if (t.kind != kind) {
        char msg[256];
        snprintf(msg, sizeof(msg),
                 "Expected token kind %d, got %d ('%s')", kind, t.kind, t.value);
        set_error(p, msg, t.start);
    }
    return t;
}

/* ------------------------------------------------------------------ */
/* ASM instruction parser - parses a single instruction line          */
/* Mirrors InstructionParser.cs                                        */
/* ------------------------------------------------------------------ */

static int parse_memory_operand(Scanner *s, Operand *out, int is_global)
{
    /* Expects "[" NUMBER "]" */
    Token t = scanner_current(s);
    if (t.kind != TOK_LBRACKET)
        return 0;
    scanner_scan(s); /* [ */
    Token num = scanner_scan(s);
    if (num.kind != TOK_NUMBER && num.kind != TOK_FLOAT)
        return 0;
    long idx = atol(num.value);
    Token rb = scanner_scan(s);
    if (rb.kind != TOK_RBRACKET)
        return 0;
    *out = op_mem(idx, is_global);
    return 1;
}

static void skip_commas(Scanner *s)
{
    while (scanner_current(s).kind == TOK_COMMA)
        scanner_scan(s);
}

/* Parse a single AGI assembly instruction from source string */
static int parse_asm_instruction(const char *text, Instruction *out)
{
    Scanner s;
    scanner_init(&s, text);

    Token opcode_tok = scanner_scan(&s);
    if (opcode_tok.kind != TOK_IDENTIFIER) {
        scanner_free(&s);
        /* Empty or nop */
        *out = make_instr(OP_NOP, op_none(), op_none());
        return 1;
    }

    char op_lower[32];
    str_lower(op_lower, opcode_tok.value, sizeof(op_lower));

    skip_commas(&s);

    Opcode opcode = OP_NOP;
    Operand op1 = op_none(), op2 = op_none();

    if (strcmp(op_lower, "nop") == 0) {
        opcode = OP_NOP;
    }
    else if (strcmp(op_lower, "ret") == 0) {
        opcode = OP_RET;
    }
    else if (strcmp(op_lower, "def") == 0) {
        opcode = OP_DEF;
    }
    else if (strcmp(op_lower, "defgen") == 0) {
        opcode = OP_DEFGEN;
    }
    else if (strcmp(op_lower, "await") == 0) {
        opcode = OP_AWAIT;
    }
    else if (strcmp(op_lower, "awaitobj") == 0) {
        opcode = OP_AWAITOBJ;
    }
    else if (strcmp(op_lower, "streamwaitobj") == 0) {
        opcode = OP_STREAMWAITOBJ;
    }
    else if (strcmp(op_lower, "streamwait") == 0) {
        opcode = OP_STREAMWAIT;
    }
    else if (strcmp(op_lower, "getobj") == 0) {
        opcode = OP_GETOBJ;
    }
    else if (strcmp(op_lower, "setobj") == 0) {
        opcode = OP_SETOBJ;
    }
    else if (strcmp(op_lower, "pop") == 0) {
        opcode = OP_POP;
        Token cur = scanner_current(&s);
        if (cur.kind == TOK_LBRACKET) {
            parse_memory_operand(&s, &op1, 0);
        }
    }
    else if (strcmp(op_lower, "push") == 0) {
        opcode = OP_PUSH;
        Token cur = scanner_current(&s);
        /* push global:[idx] */
        if (cur.kind == TOK_IDENTIFIER && str_eq_ci(cur.value, "global")) {
            scanner_scan(&s); /* global */
            scanner_scan(&s); /* : */
            parse_memory_operand(&s, &op1, 1);
        }
        /* push [idx] */
        else if (cur.kind == TOK_LBRACKET) {
            parse_memory_operand(&s, &op1, 0);
        }
        /* push string: "..." */
        else if (cur.kind == TOK_IDENTIFIER && str_eq_ci(cur.value, "string")) {
            scanner_scan(&s); /* string */
            scanner_scan(&s); /* : */
            Token str_tok = scanner_scan(&s); /* string literal */
            op1 = op_str(str_tok.value);
        }
        /* push NUMBER */
        else if (cur.kind == TOK_NUMBER) {
            Token t = scanner_scan(&s);
            op1 = op_int(atol(t.value));
        }
        /* push FLOAT */
        else if (cur.kind == TOK_FLOAT) {
            Token t = scanner_scan(&s);
            op1 = op_float(atof(t.value));
        }
        /* push IDENTIFIER (type literal) */
        else if (cur.kind == TOK_IDENTIFIER) {
            Token t = scanner_scan(&s);
            op1 = op_type(t.value);
        }
    }
    else if (strcmp(op_lower, "call") == 0 || strcmp(op_lower, "syscall") == 0) {
        opcode = (strcmp(op_lower, "syscall") == 0) ? OP_SYSCALL : OP_CALL;
        Token cur = scanner_current(&s);
        char fn_name[OPERAND_STR_MAX] = {0};
        if (cur.kind == TOK_STRING || cur.kind == TOK_IDENTIFIER) {
            Token t = scanner_scan(&s);
            strncpy(fn_name, t.value, OPERAND_STR_MAX - 1);
        }
        op1 = op_call(fn_name);
    }
    else if (strcmp(op_lower, "callobj") == 0) {
        opcode = OP_CALLOBJ;
        Token cur = scanner_current(&s);
        if (cur.kind == TOK_STRING || cur.kind == TOK_IDENTIFIER) {
            Token t = scanner_scan(&s);
            op1 = op_str(t.value);
        }
    }
    else if (strcmp(op_lower, "label") == 0) {
        opcode = OP_LABEL;
        Token cur = scanner_current(&s);
        if (cur.kind == TOK_STRING || cur.kind == TOK_IDENTIFIER) {
            Token t = scanner_scan(&s);
            op1 = op_label(t.value);
        }
    }
    else if (strcmp(op_lower, "je") == 0) {
        opcode = OP_JE;
        Token cur = scanner_current(&s);
        if (cur.kind == TOK_STRING || cur.kind == TOK_IDENTIFIER) {
            Token t = scanner_scan(&s);
            op1 = op_label(t.value);
        }
    }
    else if (strcmp(op_lower, "jmp") == 0) {
        opcode = OP_JMP;
        Token cur = scanner_current(&s);
        if (cur.kind == TOK_STRING || cur.kind == TOK_IDENTIFIER) {
            Token t = scanner_scan(&s);
            op1 = op_label(t.value);
        }
    }
    else if (strcmp(op_lower, "cmp") == 0) {
        opcode = OP_CMP;
        /* cmp [idx], value or cmp [idx] number */
        Token cur = scanner_current(&s);
        if (cur.kind == TOK_LBRACKET) {
            parse_memory_operand(&s, &op1, 0);
            skip_commas(&s);
            cur = scanner_current(&s);
            if (cur.kind == TOK_NUMBER || cur.kind == TOK_FLOAT) {
                Token t = scanner_scan(&s);
                op2 = op_int(atol(t.value));
            }
        }
    }
    else if (strcmp(op_lower, "addvertex") == 0 ||
             strcmp(op_lower, "addrelation") == 0 ||
             strcmp(op_lower, "addshape") == 0)
    {
        /* Parse data: name: "value", index: N, etc. */
        if (strcmp(op_lower, "addvertex") == 0)        opcode = OP_ADDVERTEX;
        else if (strcmp(op_lower, "addrelation") == 0)  opcode = OP_ADDRELATION;
        else                                             opcode = OP_ADDSHAPE;
        /* Collect remaining line as data string */
        char data_buf[OPERAND_STR_MAX] = {0};
        int  dlen = 0;
        Token t;
        while ((t = scanner_current(&s)).kind != TOK_EOF && t.kind != TOK_NEWLINE) {
            if (dlen + strlen(t.value) + 2 < OPERAND_STR_MAX) {
                if (dlen > 0) data_buf[dlen++] = ' ';
                strncpy(data_buf + dlen, t.value, OPERAND_STR_MAX - dlen - 1);
                dlen += (int)strlen(t.value);
            }
            scanner_scan(&s);
        }
        op1 = op_str(data_buf);
    }
    else if (strcmp(op_lower, "move") == 0) {
        /* move [dst], [src] or move [dst], value */
        opcode = OP_MOVE;
        Token cur = scanner_current(&s);
        if (cur.kind == TOK_LBRACKET) {
            parse_memory_operand(&s, &op1, 0);
            skip_commas(&s);
            cur = scanner_current(&s);
            if (cur.kind == TOK_LBRACKET) {
                parse_memory_operand(&s, &op2, 0);
            } else if (cur.kind == TOK_NUMBER) {
                Token t = scanner_scan(&s);
                op2 = op_int(atol(t.value));
            } else if (cur.kind == TOK_STRING || cur.kind == TOK_IDENTIFIER) {
                Token t = scanner_scan(&s);
                op2 = op_str(t.value);
            }
        }
    }
    else if (strcmp(op_lower, "getvertex") == 0) {
        opcode = OP_GETVERTEX;
        Token cur = scanner_current(&s);
        if (cur.kind == TOK_NUMBER) {
            Token t = scanner_scan(&s);
            op1 = op_int(atol(t.value));
        } else if (cur.kind == TOK_STRING || cur.kind == TOK_IDENTIFIER) {
            Token t = scanner_scan(&s);
            op1 = op_str(t.value);
        }
    }
    else if (strcmp(op_lower, "acall") == 0) {
        opcode = OP_ACALL;
        Token cur = scanner_current(&s);
        if (cur.kind == TOK_STRING || cur.kind == TOK_IDENTIFIER) {
            Token t = scanner_scan(&s);
            op1 = op_call(t.value);
        }
    }
    else if (strcmp(op_lower, "expr") == 0) {
        opcode = OP_EXPR;
        Token cur = scanner_current(&s);
        if (cur.kind == TOK_NUMBER) {
            Token t = scanner_scan(&s);
            op1 = op_int(atol(t.value));
        }
    }
    else if (strcmp(op_lower, "defexpr") == 0) {
        opcode = OP_DEFEXPR;
    }
    else if (strcmp(op_lower, "lambda") == 0) {
        opcode = OP_LAMBDA;
    }
    else if (strcmp(op_lower, "equals") == 0) {
        opcode = OP_EQUALS;
    }
    else if (strcmp(op_lower, "not") == 0) {
        opcode = OP_NOT;
    }
    else if (strcmp(op_lower, "lt") == 0) {
        opcode = OP_LT;
    }
    else if (strcmp(op_lower, "add") == 0) {
        opcode = OP_ADD;
    }
    else if (strcmp(op_lower, "sub") == 0) {
        opcode = OP_SUB;
    }
    else if (strcmp(op_lower, "mul") == 0) {
        opcode = OP_MUL;
    }
    else if (strcmp(op_lower, "div") == 0) {
        opcode = OP_DIV;
    }
    else if (strcmp(op_lower, "pow") == 0) {
        opcode = OP_POW;
    }
    else if (strcmp(op_lower, "defobj") == 0) {
        opcode = OP_DEFOBJ;
        Token cur = scanner_current(&s);
        if (cur.kind == TOK_STRING || cur.kind == TOK_IDENTIFIER) {
            Token t = scanner_scan(&s);
            op1 = op_type(t.value);
        }
    }
    else {
        /* Unknown opcode: treat as NOP */
        opcode = OP_NOP;
    }

    *out = make_instr(opcode, op1, op2);
    scanner_free(&s);
    return 1;
}

/* ------------------------------------------------------------------ */
/* Statement lowering - transforms high-level statements to asm       */
/* Simplified version of StatementLoweringCompiler.cs                 */
/* ------------------------------------------------------------------ */

typedef struct {
    const char *source;
    Block      *block;
    CompileResult *result;
} LoweringCtx;

/* Helper: add instructions for a "print" statement */
static void lower_print_call(LoweringCtx *ctx, const char *arg)
{
    /* push string: "arg"
       push 1       (arity)
       call print   */
    block_add(ctx->block, make_instr(OP_PUSH, op_str(arg), op_none()));
    block_add(ctx->block, make_instr(OP_PUSH, op_int(1), op_none()));
    block_add(ctx->block, make_instr(OP_CALL, op_call("print"), op_none()));
}

/* Parse a high-level statement and lower it to assembly instructions */
static int lower_statement(LoweringCtx *ctx, const char *stmt)
{
    /* Trim whitespace */
    while (*stmt == ' ' || *stmt == '\t') stmt++;
    if (!*stmt) return 1;

    Scanner s;
    scanner_init(&s, stmt);
    Token first = scanner_current(&s);

    /* Check if it looks like an ASM opcode */
    static const char *asm_opcodes[] = {
        "nop", "push", "pop", "call", "ret", "syscall",
        "def", "defgen", "defobj", "callobj", "awaitobj", "await",
        "streamwaitobj", "streamwait", "label", "cmp", "je", "jmp",
        "getobj", "setobj", "addvertex", "addrelation", "addshape",
        "move", "getvertex", "acall", "expr", "defexpr", "lambda",
        "equals", "not", "lt", "add", "sub", "mul", "div", "pow",
        NULL
    };

    if (first.kind == TOK_IDENTIFIER) {
        char lower[64];
        str_lower(lower, first.value, sizeof(lower));
        for (int i = 0; asm_opcodes[i]; i++) {
            if (strcmp(lower, asm_opcodes[i]) == 0) {
                scanner_free(&s);
                Instruction instr;
                if (parse_asm_instruction(stmt, &instr))
                    block_add(ctx->block, instr);
                return 1;
            }
        }
    }

    scanner_free(&s);

    /* High-level statements: treat entire statement as a function call
       This handles patterns like:
         functionName;                     -> call functionName (push 0; call fn)
         functionName arg1, arg2           -> push args; push arity; call fn
       For the initial port, parse simple assignment and call patterns.
    */

    /* Try: identifier ";"  or just identifier -> bare call */
    Scanner s2;
    scanner_init(&s2, stmt);
    Token id = scanner_current(&s2);
    if (id.kind == TOK_IDENTIFIER) {
        scanner_scan(&s2);
        Token next = scanner_current(&s2);
        /* Bare call: name; or end of input */
        if (next.kind == TOK_EOF || next.kind == TOK_SEMICOLON || next.kind == TOK_NEWLINE) {
            block_add(ctx->block, make_instr(OP_PUSH, op_int(0), op_none())); /* arity 0 */
            block_add(ctx->block, make_instr(OP_CALL, op_call(id.value), op_none()));
            scanner_free(&s2);
            return 1;
        }
        /* Check for assignment: varname = expression */
        if (next.kind == TOK_ASSIGN) {
            /* Simple string assignment: var = "value" */
            scanner_scan(&s2); /* consume = */
            Token val = scanner_current(&s2);
            if (val.kind == TOK_STRING) {
                /* push string: "val"; pop [slot] - use djb2 hash as slot */
                /* For simplicity, we just store statements inline */
                /* This is a simplified lowering for basic .agi programs */
                scanner_free(&s2);
                return 1; /* skip for now - advanced feature */
            }
        }
    }
    scanner_free(&s2);

    /* Last resort: try to parse as assembly instruction */
    Instruction instr;
    if (parse_asm_instruction(stmt, &instr)) {
        block_add(ctx->block, instr);
        return 1;
    }

    return 1; /* silently skip unknown high-level statements */
}

/* ------------------------------------------------------------------ */
/* Block parser helpers                                                */
/* ------------------------------------------------------------------ */

/* Checks if token is an ASM opcode keyword */
static int is_asm_opcode(const char *val)
{
    static const char *ops[] = {
        "addvertex", "addrelation", "addshape", "call", "pop", "push",
        "nop", "def", "defgen", "defobj", "callobj", "awaitobj", "streamwaitobj",
        "await", "label", "cmp", "je", "jmp", "getobj", "setobj",
        "streamwait", "ret", "syscall", "move", "getvertex", "acall",
        "expr", "defexpr", "lambda", "equals", "not", "lt",
        "add", "sub", "mul", "div", "pow", NULL
    };
    char low[64];
    str_lower(low, val, sizeof(low));
    for (int i = 0; ops[i]; i++)
        if (strcmp(low, ops[i]) == 0) return 1;
    return 0;
}

static int is_program_keyword(const char *val)
{
    return str_eq_ci(val, "@") ||
           str_eq_ci(val, "AGI") ||
           str_eq_ci(val, "program") ||
           str_eq_ci(val, "module") ||
           str_eq_ci(val, "system") ||
           str_eq_ci(val, "procedure") ||
           str_eq_ci(val, "function") ||
           str_eq_ci(val, "entrypoint") ||
           str_eq_ci(val, "use") ||
           str_eq_ci(val, "asm");
}

/* Statement buffer for body lowering (was 2048x2048 = 4MB static — brutal in kernel). */
#define PARSE_STMT_LINE_MAX 1024
#define PARSE_BODY_MAX_STMTS 256

/* Consume a block and return statement strings.
   Assumes opening "{" was already consumed.
   If asm_mode: split on newlines when next token starts opcode.
   Otherwise: split on newlines and semicolons.
   Returns count of statements added to stmts (up to max_stmts). */
static int consume_block(Parser *p, char stmts[][PARSE_STMT_LINE_MAX], int max_stmts,
                           int asm_mode)
{
    int count  = 0;
    int depth  = 1;
    char buf[4096];
    int  blen  = 0;

    /* Helper: flush current buffer as a statement */
#define FLUSH_STMT() do { \
    if (blen > 0) { \
        buf[blen] = '\0'; \
        /* trim */ \
        int _i = 0; while (buf[_i] == ' ' || buf[_i] == '\t') _i++; \
        if (buf[_i] && count < max_stmts) { \
            strncpy(stmts[count++], buf + _i, PARSE_STMT_LINE_MAX - 1); \
            stmts[count - 1][PARSE_STMT_LINE_MAX - 1] = '\0'; \
        } \
        blen = 0; \
    } \
} while(0)

    while (1) {
        Token t = scanner_scan(&p->scanner);
        if (t.kind == TOK_EOF) {
            FLUSH_STMT();
            break;
        }
        if (t.kind == TOK_LBRACE) {
            depth++;
            if (blen + 1 < (int)sizeof(buf)) buf[blen++] = '{';
            continue;
        }
        if (t.kind == TOK_RBRACE) {
            depth--;
            if (depth == 0) {
                FLUSH_STMT();
                break;
            }
            if (blen + 1 < (int)sizeof(buf)) buf[blen++] = '}';
            continue;
        }
        if (depth == 1 && !asm_mode &&
            (t.kind == TOK_SEMICOLON || t.kind == TOK_NEWLINE))
        {
            FLUSH_STMT();
            continue;
        }
        if (depth == 1 && asm_mode && t.kind == TOK_NEWLINE) {
            /* Flush only if next non-newline token is an asm opcode */
            int saved = scanner_save(&p->scanner);
            /* peek forward */
            Token peek;
            do {
                peek = scanner_current(&p->scanner);
                if (peek.kind == TOK_NEWLINE) scanner_scan(&p->scanner);
                else break;
            } while (1);
            int starts_opcode = (peek.kind == TOK_IDENTIFIER && is_asm_opcode(peek.value));
            int starts_rbrace  = (peek.kind == TOK_RBRACE);
            scanner_restore(&p->scanner, saved);
            if (starts_opcode || starts_rbrace) {
                FLUSH_STMT();
            } else {
                if (blen + 1 < (int)sizeof(buf)) buf[blen++] = '\n';
            }
            continue;
        }
        if (depth == 1 && asm_mode && t.kind == TOK_SEMICOLON) {
            FLUSH_STMT();
            continue;
        }
        /* Append token value + space to buf */
        int vlen = (int)strlen(t.value);
        if (t.kind == TOK_STRING) {
            /* Re-wrap in quotes */
            if (blen + vlen + 4 < (int)sizeof(buf)) {
                buf[blen++] = '"';
                memcpy(buf + blen, t.value, vlen);
                blen += vlen;
                buf[blen++] = '"';
                buf[blen++] = ' ';
            }
        } else {
            if (blen + vlen + 2 < (int)sizeof(buf)) {
                memcpy(buf + blen, t.value, vlen);
                blen += vlen;
                buf[blen++] = ' ';
            }
        }
    }
#undef FLUSH_STMT
    return count;
}

/* Parse a body block { ... } and fill block with instructions */
static int parse_body_block(Parser *p, Block *block)
{
    Token cur = scanner_current(&p->scanner);
    if (cur.kind != TOK_LBRACE)
        return 0;
    scanner_scan(&p->scanner); /* consume { */
    skip_newlines(p);

    /* Check for asm { ... } sub-block */
    cur = scanner_current(&p->scanner);
    int asm_mode = 0;
    if (cur.kind == TOK_IDENTIFIER && str_eq_ci(cur.value, "asm")) {
        scanner_scan(&p->scanner); /* consume asm */
        skip_newlines(p);
        cur = scanner_current(&p->scanner);
        if (cur.kind != TOK_LBRACE) {
            set_error(p, "Expected '{' after asm", cur.start);
            return 0;
        }
        scanner_scan(&p->scanner); /* consume asm { */
        asm_mode = 1;
    }

    /* Collect statement strings (bounded; was 4MB static) */
    static char stmts[PARSE_BODY_MAX_STMTS][PARSE_STMT_LINE_MAX];
    int count = consume_block(p, stmts, PARSE_BODY_MAX_STMTS, asm_mode);

    if (asm_mode) {
        /* consume outer } */
        skip_newlines(p);
        Token closing = scanner_current(&p->scanner);
        if (closing.kind == TOK_RBRACE)
            scanner_scan(&p->scanner);
    }

    /* Lower each statement to instructions */
    LoweringCtx ctx;
    ctx.source = p->source;
    ctx.block  = block;
    ctx.result = p->result;

    for (int i = 0; i < count; i++) {
        if (asm_mode) {
            Instruction instr;
            if (parse_asm_instruction(stmts[i], &instr))
                block_add(block, instr);
        } else {
            lower_statement(&ctx, stmts[i]);
        }
    }
    return 1;
}

/* ------------------------------------------------------------------ */
/* Program structure parser                                            */
/* Mirrors Parser.cs ParseProgram()                                    */
/* ------------------------------------------------------------------ */

static int try_parse_agi(Parser *p, ExecutableUnit *unit)
{
    Token cur = scanner_current(&p->scanner);
    if (cur.kind != TOK_IDENTIFIER || strcmp(cur.value, "@") != 0)
        return 0;
    scanner_scan(&p->scanner); /* @ */
    Token agi = scanner_scan(&p->scanner); /* AGI */
    if (!str_eq_ci(agi.value, "AGI")) {
        set_error(p, "Expected 'AGI' after '@'", agi.start);
        return 0;
    }
    /* Read version until newline */
    char ver[NAME_MAX_LEN] = {0};
    int  vlen = 0;
    while (scanner_current(&p->scanner).kind != TOK_NEWLINE &&
           scanner_current(&p->scanner).kind != TOK_EOF) {
        Token t = scanner_scan(&p->scanner);
        if (vlen + (int)strlen(t.value) + 2 < NAME_MAX_LEN) {
            if (vlen > 0) ver[vlen++] = ' ';
            strncpy(ver + vlen, t.value, NAME_MAX_LEN - vlen - 1);
            vlen += (int)strlen(t.value);
        }
    }
    if (scanner_current(&p->scanner).kind == TOK_NEWLINE)
        scanner_scan(&p->scanner);
    strncpy(unit->version, ver, NAME_MAX_LEN - 1);
    return 1;
}

static int try_parse_program(Parser *p, ExecutableUnit *unit)
{
    Token cur = scanner_current(&p->scanner);
    if (cur.kind != TOK_IDENTIFIER || !str_eq_ci(cur.value, "program"))
        return 0;
    scanner_scan(&p->scanner);
    /* Read name until newline or semicolon */
    char name[NAME_MAX_LEN] = {0};
    int  nlen = 0;
    while (scanner_current(&p->scanner).kind != TOK_NEWLINE &&
           scanner_current(&p->scanner).kind != TOK_SEMICOLON &&
           scanner_current(&p->scanner).kind != TOK_EOF) {
        Token t = scanner_scan(&p->scanner);
        if (nlen + (int)strlen(t.value) + 2 < NAME_MAX_LEN) {
            if (nlen > 0) name[nlen++] = ' ';
            strncpy(name + nlen, t.value, NAME_MAX_LEN - nlen - 1);
            nlen += (int)strlen(t.value);
        }
    }
    if (scanner_current(&p->scanner).kind == TOK_SEMICOLON)
        scanner_scan(&p->scanner);
    skip_newlines(p);
    strncpy(unit->name, name, NAME_MAX_LEN - 1);
    return 1;
}

static int try_parse_module(Parser *p, ExecutableUnit *unit)
{
    Token cur = scanner_current(&p->scanner);
    if (cur.kind != TOK_IDENTIFIER || !str_eq_ci(cur.value, "module"))
        return 0;
    scanner_scan(&p->scanner);
    char name[NAME_MAX_LEN] = {0};
    int  nlen = 0;
    while (scanner_current(&p->scanner).kind != TOK_NEWLINE &&
           scanner_current(&p->scanner).kind != TOK_SEMICOLON &&
           scanner_current(&p->scanner).kind != TOK_EOF) {
        Token t = scanner_scan(&p->scanner);
        if (nlen + (int)strlen(t.value) + 2 < NAME_MAX_LEN) {
            if (nlen > 0) name[nlen++] = ' ';
            strncpy(name + nlen, t.value, NAME_MAX_LEN - nlen - 1);
            nlen += (int)strlen(t.value);
        }
    }
    if (scanner_current(&p->scanner).kind == TOK_SEMICOLON)
        scanner_scan(&p->scanner);
    skip_newlines(p);
    strncpy(unit->module, name, NAME_MAX_LEN - 1);
    return 1;
}

static int try_parse_system(Parser *p, ExecutableUnit *unit)
{
    Token cur = scanner_current(&p->scanner);
    if (cur.kind != TOK_IDENTIFIER || !str_eq_ci(cur.value, "system"))
        return 0;
    scanner_scan(&p->scanner);
    char name[NAME_MAX_LEN] = {0};
    int  nlen = 0;
    while (scanner_current(&p->scanner).kind != TOK_NEWLINE &&
           scanner_current(&p->scanner).kind != TOK_SEMICOLON &&
           scanner_current(&p->scanner).kind != TOK_EOF) {
        Token t = scanner_scan(&p->scanner);
        if (nlen + (int)strlen(t.value) + 2 < NAME_MAX_LEN) {
            if (nlen > 0) name[nlen++] = ' ';
            strncpy(name + nlen, t.value, NAME_MAX_LEN - nlen - 1);
            nlen += (int)strlen(t.value);
        }
    }
    if (scanner_current(&p->scanner).kind == TOK_SEMICOLON)
        scanner_scan(&p->scanner);
    skip_newlines(p);
    strncpy(unit->system_name, name, NAME_MAX_LEN - 1);
    return 1;
}

/* ------------------------------------------------------------------ */
/* use directive parser                                                 */
/* Mirrors Parser.cs TryParseUse()                                     */
/* Syntax:                                                             */
/*   use <module_path>;                                                */
/*   use <module_path> as <alias>;                                     */
/*   use <module_path> as { function f1(x, y); procedure p1(); };     */
/* Path separators: ':' or '\' — all converted to '\' internally.     */
/* ------------------------------------------------------------------ */
static int try_parse_use(Parser *p, ExecutableUnit *unit)
{
    (void)unit; /* use directives do not modify the unit during parse */
    Token cur = scanner_current(&p->scanner);
    if (cur.kind != TOK_IDENTIFIER || !str_eq_ci(cur.value, "use"))
        return 0;

    scanner_scan(&p->scanner); /* consume 'use' */
    skip_newlines(p);

    /* Build module path from segments separated by ':', '\', or '/' */
    char path[USE_PATH_MAX] = {0};
    int  plen = 0;

    cur = scanner_current(&p->scanner);
    if (cur.kind != TOK_IDENTIFIER || str_eq_ci(cur.value, "as"))
        return 0; /* nothing after 'use' */

    /* First segment */
    strncpy(path + plen, cur.value, USE_PATH_MAX - plen - 1);
    plen += (int)strlen(cur.value);
    scanner_scan(&p->scanner);
    skip_newlines(p);

    /* Continue while we see path separators */
    while (1) {
        cur = scanner_current(&p->scanner);
        int is_sep = 0;
        if (cur.kind == TOK_COLON)
            is_sep = 1;
        else if (cur.kind == TOK_IDENTIFIER &&
                 (strcmp(cur.value, "\\") == 0 || strcmp(cur.value, "/") == 0))
            is_sep = 1;
        if (!is_sep)
            break;

        scanner_scan(&p->scanner); /* consume separator */
        skip_newlines(p);
        cur = scanner_current(&p->scanner);
        if (cur.kind != TOK_IDENTIFIER || str_eq_ci(cur.value, "as"))
            break;

        if (plen + 1 < USE_PATH_MAX - 1) {
            path[plen++] = '\\';
            strncpy(path + plen, cur.value, USE_PATH_MAX - plen - 1);
            plen += (int)strlen(cur.value);
        }
        scanner_scan(&p->scanner);
        skip_newlines(p);
    }

    /* Consume optional semicolons before 'as' */
    while (scanner_current(&p->scanner).kind == TOK_SEMICOLON)
        scanner_scan(&p->scanner);
    skip_newlines(p);

    /* If we have a use directive set, record it */
    if (p->uses && p->uses->count < USE_MAX) {
        UseDirective *ud = &p->uses->items[p->uses->count];
        memset(ud, 0, sizeof(*ud));
        strncpy(ud->module_path, path, USE_PATH_MAX - 1);

        /* Derive default alias: last segment of path */
        const char *last_sep = strrchr(path, '\\');
        const char *alias_start = last_sep ? last_sep + 1 : path;
        strncpy(ud->alias, alias_start, USE_SIG_NAME_MAX - 1);

        cur = scanner_current(&p->scanner);
        if (cur.kind == TOK_IDENTIFIER && str_eq_ci(cur.value, "as")) {
            scanner_scan(&p->scanner); /* consume 'as' */
            skip_newlines(p);
            cur = scanner_current(&p->scanner);

            if (cur.kind == TOK_LBRACE) {
                /* use <path> as { function f1(x,y); ... }; */
                ud->has_as_block = 1;
                scanner_scan(&p->scanner); /* consume '{' */
                skip_newlines(p);

                while (scanner_current(&p->scanner).kind != TOK_RBRACE &&
                       scanner_current(&p->scanner).kind != TOK_EOF) {
                    skip_newlines(p);
                    while (scanner_current(&p->scanner).kind == TOK_SEMICOLON)
                        scanner_scan(&p->scanner);
                    skip_newlines(p);

                    if (scanner_current(&p->scanner).kind == TOK_RBRACE)
                        break;

                    Token kind_tok = scanner_current(&p->scanner);
                    if (kind_tok.kind != TOK_IDENTIFIER) {
                        scanner_scan(&p->scanner); /* skip unknown token */
                        continue;
                    }
                    int is_fn = str_eq_ci(kind_tok.value, "function");
                    int is_pr = str_eq_ci(kind_tok.value, "procedure");
                    if (!is_fn && !is_pr) {
                        /* unknown keyword in as-block, skip line */
                        while (scanner_current(&p->scanner).kind != TOK_SEMICOLON &&
                               scanner_current(&p->scanner).kind != TOK_NEWLINE &&
                               scanner_current(&p->scanner).kind != TOK_RBRACE &&
                               scanner_current(&p->scanner).kind != TOK_EOF)
                            scanner_scan(&p->scanner);
                        continue;
                    }
                    scanner_scan(&p->scanner); /* consume 'function'/'procedure' */

                    Token name_tok = scanner_current(&p->scanner);
                    if (name_tok.kind != TOK_IDENTIFIER) continue;
                    scanner_scan(&p->scanner); /* consume name */

                    /* Skip parameter list (...) */
                    if (scanner_current(&p->scanner).kind == TOK_LPAREN) {
                        int depth = 1;
                        scanner_scan(&p->scanner); /* consume '(' */
                        while (depth > 0 && scanner_current(&p->scanner).kind != TOK_EOF) {
                            Token t = scanner_scan(&p->scanner);
                            if (t.kind == TOK_LPAREN) depth++;
                            else if (t.kind == TOK_RPAREN) depth--;
                        }
                    }

                    while (scanner_current(&p->scanner).kind == TOK_SEMICOLON)
                        scanner_scan(&p->scanner);

                    if (ud->sig_count < USE_SIG_MAX) {
                        UseSignature *sig = &ud->sigs[ud->sig_count++];
                        strncpy(sig->name, name_tok.value, USE_SIG_NAME_MAX - 1);
                        sig->is_function = is_fn;
                    }
                }
                /* consume '}' */
                if (scanner_current(&p->scanner).kind == TOK_RBRACE)
                    scanner_scan(&p->scanner);
            } else if (cur.kind == TOK_IDENTIFIER) {
                /* use <path> as <alias>; */
                strncpy(ud->alias, cur.value, USE_SIG_NAME_MAX - 1);
                scanner_scan(&p->scanner);
            }
        }

        p->uses->count++;
    } else {
        /* No use set, or too many uses — just consume 'as' block if present */
        cur = scanner_current(&p->scanner);
        if (cur.kind == TOK_IDENTIFIER && str_eq_ci(cur.value, "as")) {
            scanner_scan(&p->scanner);
            skip_newlines(p);
            if (scanner_current(&p->scanner).kind == TOK_LBRACE) {
                int depth = 1;
                scanner_scan(&p->scanner);
                while (depth > 0 && scanner_current(&p->scanner).kind != TOK_EOF) {
                    Token t = scanner_scan(&p->scanner);
                    if (t.kind == TOK_LBRACE) depth++;
                    else if (t.kind == TOK_RBRACE) depth--;
                }
            } else if (scanner_current(&p->scanner).kind == TOK_IDENTIFIER) {
                scanner_scan(&p->scanner); /* alias */
            }
        }
    }

    skip_newlines(p);
    while (scanner_current(&p->scanner).kind == TOK_SEMICOLON)
        scanner_scan(&p->scanner);
    skip_newlines(p);
    return 1;
}

/* Skip optional parameter list "(param1, param2, ...)" after function/procedure name */
static void skip_param_list(Parser *p)
{
    if (scanner_current(&p->scanner).kind != TOK_LPAREN)
        return;
    int depth = 1;
    scanner_scan(&p->scanner); /* consume '(' */
    while (depth > 0 && scanner_current(&p->scanner).kind != TOK_EOF) {
        Token t = scanner_scan(&p->scanner);
        if (t.kind == TOK_LPAREN)       depth++;
        else if (t.kind == TOK_RPAREN)  depth--;
    }
}

static int try_parse_procedure(Parser *p, ExecutableUnit *unit)
{
    Token cur = scanner_current(&p->scanner);
    if (cur.kind != TOK_IDENTIFIER || !str_eq_ci(cur.value, "procedure"))
        return 0;
    scanner_scan(&p->scanner);
    Token name_tok = expect_token(p, TOK_IDENTIFIER);
    if (!p->result->success) return 0;
    skip_param_list(p); /* skip optional "(param1, ...)" */
    skip_newlines(p);

    if (unit->proc_count >= MAX_PROCS) {
        set_error(p, "Too many procedures", cur.start);
        return 0;
    }

    Procedure *proc = &unit->procedures[unit->proc_count];
    strncpy(proc->name, name_tok.value, PROC_NAME_MAX - 1);
    block_init(&proc->body);

    if (!parse_body_block(p, &proc->body)) {
        set_error(p, "Expected body block for procedure", cur.start);
        return 0;
    }
    unit->proc_count++;
    return 1;
}

static int try_parse_function(Parser *p, ExecutableUnit *unit)
{
    Token cur = scanner_current(&p->scanner);
    if (cur.kind != TOK_IDENTIFIER || !str_eq_ci(cur.value, "function"))
        return 0;
    scanner_scan(&p->scanner);
    Token name_tok = expect_token(p, TOK_IDENTIFIER);
    if (!p->result->success) return 0;
    skip_param_list(p); /* skip optional "(param1, ...)" */
    skip_newlines(p);

    if (unit->func_count >= MAX_PROCS) {
        set_error(p, "Too many functions", cur.start);
        return 0;
    }

    Procedure *func = &unit->functions[unit->func_count];
    strncpy(func->name, name_tok.value, PROC_NAME_MAX - 1);
    block_init(&func->body);

    if (!parse_body_block(p, &func->body)) {
        set_error(p, "Expected body block for function", cur.start);
        return 0;
    }
    unit->func_count++;
    return 1;
}

static int try_parse_entrypoint(Parser *p, ExecutableUnit *unit)
{
    Token cur = scanner_current(&p->scanner);
    if (cur.kind != TOK_IDENTIFIER || !str_eq_ci(cur.value, "entrypoint"))
        return 0;
    scanner_scan(&p->scanner);
    skip_newlines(p);

    if (!parse_body_block(p, &unit->entry_point)) {
        set_error(p, "Expected body block for entrypoint", cur.start);
        return 0;
    }
    return 1;
}

/* top-level call: "funcname ;" */
static int try_parse_toplevel_call(Parser *p, ExecutableUnit *unit)
{
    Token cur = scanner_current(&p->scanner);
    if (cur.kind != TOK_IDENTIFIER || is_program_keyword(cur.value))
        return 0;

    Token *next = scanner_watch(&p->scanner, 1);
    if (!next || (next->kind != TOK_SEMICOLON && next->kind != TOK_NEWLINE))
        return 0;

    scanner_scan(&p->scanner); /* identifier */
    scanner_scan(&p->scanner); /* ; or newline */

    block_add(&unit->entry_point,
              make_instr(OP_PUSH, op_int(0), op_none())); /* arity 0 */
    block_add(&unit->entry_point,
              make_instr(OP_CALL, op_call(cur.value), op_none()));
    skip_newlines(p);
    return 1;
}

/* Flat instruction set: parse source as list of assembly lines */
static void parse_as_instruction_set(Parser *p, Block *block)
{
    /* Re-scan source line by line */
    const char *src = p->source;
    char line_buf[2048];
    int  line_len = 0;

    for (int i = 0; src[i]; i++) {
        char c = src[i];
        if (c == '\n' || c == '\r') {
            line_buf[line_len] = '\0';
            /* trim */
            int s = 0;
            while (line_buf[s] == ' ' || line_buf[s] == '\t') s++;
            if (line_buf[s] && line_buf[s] != '/' ) { /* skip comment lines */
                Instruction instr;
                if (parse_asm_instruction(line_buf + s, &instr))
                    block_add(block, instr);
            }
            line_len = 0;
            if (c == '\r' && src[i + 1] == '\n') i++;
        } else {
            if (line_len < 2047)
                line_buf[line_len++] = c;
        }
    }
    /* Handle last line */
    if (line_len > 0) {
        line_buf[line_len] = '\0';
        int s = 0;
        while (line_buf[s] == ' ' || line_buf[s] == '\t') s++;
        if (line_buf[s]) {
            Instruction instr;
            if (parse_asm_instruction(line_buf + s, &instr))
                block_add(block, instr);
        }
    }
}

/* Skip until end-of-line */
static void consume_line(Parser *p)
{
    while (scanner_current(&p->scanner).kind != TOK_NEWLINE &&
           scanner_current(&p->scanner).kind != TOK_EOF)
        scanner_scan(&p->scanner);
    if (scanner_current(&p->scanner).kind == TOK_NEWLINE)
        scanner_scan(&p->scanner);
}

/* ------------------------------------------------------------------ */
/* Main compilation entry point                                        */
/* ------------------------------------------------------------------ */

/* Internal compile: like magic_compile_source but also collects use directives */
static int compile_source_internal(const char *source, CompileResult *out, UseDirectiveSet *uses)
{
    memset(out, 0, sizeof(*out));
    out->success = 1;

    ExecutableUnit *unit = &out->unit;
    memset(unit, 0, sizeof(*unit));
    block_init(&unit->entry_point);

    if (!source || !*source) {
        return 1;
    }

    Parser p;
    memset(&p, 0, sizeof(p));
    p.result = out;
    p.source = source;
    p.uses   = uses;

    scanner_init(&p.scanner, source);

    int is_structured = 0;
    int unprocessed_lines = 0;

    while (scanner_current(&p.scanner).kind != TOK_EOF) {
        skip_newlines(&p);
        if (scanner_current(&p.scanner).kind == TOK_EOF)
            break;
        if (!out->success) break;

        if (try_parse_agi(&p, unit))          { is_structured = 1; continue; }
        if (try_parse_program(&p, unit))       { is_structured = 1; continue; }
        if (try_parse_module(&p, unit))        { is_structured = 1; continue; }
        if (try_parse_system(&p, unit))        { is_structured = 1; continue; }
        if (try_parse_use(&p, unit))           { is_structured = 1; continue; }
        if (try_parse_procedure(&p, unit))     { is_structured = 1; continue; }
        if (try_parse_function(&p, unit))      { is_structured = 1; continue; }
        if (try_parse_entrypoint(&p, unit))    { is_structured = 1; continue; }
        if (try_parse_toplevel_call(&p, unit)) { is_structured = 1; continue; }

        /* Unrecognized line */
        consume_line(&p);
        unprocessed_lines++;
    }

    /* If no structured constructs found, treat whole file as flat instruction set */
    if (!is_structured) {
        parse_as_instruction_set(&p, &unit->entry_point);
    }

    /* Build space name */
    {
        char *sp = unit->space_name;
        int   slen = 0;
        if (unit->system_name[0]) {
            strncpy(sp + slen, unit->system_name, NAME_MAX_LEN * 3 - slen - 1);
            slen += (int)strlen(unit->system_name);
        }
        if (unit->module[0]) {
            if (slen > 0 && slen < NAME_MAX_LEN * 3 - 1) sp[slen++] = '|';
            strncpy(sp + slen, unit->module, NAME_MAX_LEN * 3 - slen - 1);
            slen += (int)strlen(unit->module);
        }
        if (unit->name[0]) {
            if (slen > 0 && slen < NAME_MAX_LEN * 3 - 1) sp[slen++] = '|';
            strncpy(sp + slen, unit->name, NAME_MAX_LEN * 3 - slen - 1);
        }
    }

    scanner_free(&p.scanner);
    return out->success;
}

/* Public compile-from-source (no use-directive linking) */
int magic_compile_source(const char *source, CompileResult *out)
{
    return compile_source_internal(source, out, NULL);
}

/* Forward declaration (defined in File I/O section below) */
static char *read_file(const char *path, long *out_size);

/* ------------------------------------------------------------------ */
/* Linker: resolve 'use' directives and merge imported functions       */
/* Mirrors Magic_Kernel_Dotnet/Magic.Kernel/Compilation/Linker.cs     */
/* ------------------------------------------------------------------ */

/* Build the file path for a module given the importing file's directory
   and the module path (e.g. "modularity\module1").
   Tries <dir>/<module_path>.agi and <dir>/<last_segment>.agi. */
static int resolve_module_path(const char *importer_path,
                               const char *module_path,
                               char *out, int outlen)
{
    /* Get directory of the importer file */
    char dir[USE_PATH_MAX] = {0};
    const char *last_slash = NULL;
    {
        const char *p2 = importer_path;
        while (*p2) {
            if (*p2 == '/' || *p2 == '\\') last_slash = p2;
            p2++;
        }
    }
    if (last_slash) {
        int dlen = (int)(last_slash - importer_path);
        if (dlen >= USE_PATH_MAX) dlen = USE_PATH_MAX - 1;
        strncpy(dir, importer_path, dlen);
        dir[dlen] = '\0';
    } else {
        dir[0] = '.'; dir[1] = '\0';
    }

    /* Convert backslashes in module_path to OS separator, try full path first */
    char mp_norm[USE_PATH_MAX] = {0};
    int mlen = (int)strlen(module_path);
    for (int i = 0; i < mlen && i < USE_PATH_MAX - 1; i++)
        mp_norm[i] = (module_path[i] == '\\') ? '/' : module_path[i];

    /* Try <dir>/<normalized_module_path>.agi */
    snprintf(out, outlen, "%s/%s.agi", dir, mp_norm);
    {
        FILE *f = fopen(out, "rb");
        if (f) { fclose(f); return 1; }
    }

    /* Try <dir>/<last_segment>.agi */
    const char *last_seg = strrchr(mp_norm, '/');
    if (last_seg) last_seg++;
    else          last_seg = mp_norm;
    snprintf(out, outlen, "%s/%s.agi", dir, last_seg);
    {
        FILE *f = fopen(out, "rb");
        if (f) { fclose(f); return 1; }
    }

    return 0; /* not found */
}

/* Copy a function/procedure from src into dst with a qualified name.
   qualified_name is the new name to use (e.g. "module1:add"). */
static int merge_function(ExecutableUnit *dst, const Procedure *src,
                          const char *qualified_name, int is_function)
{
    Procedure *arr = is_function ? dst->functions : dst->procedures;
    int *count     = is_function ? &dst->func_count : &dst->proc_count;

    if (*count >= MAX_PROCS)
        return 0;

    Procedure *dest = &arr[*count];
    strncpy(dest->name, qualified_name, PROC_NAME_MAX - 1);
    dest->name[PROC_NAME_MAX - 1] = '\0';

    /* Deep-copy the block */
    block_init(&dest->body);
    for (int i = 0; i < src->body.count; i++) {
        if (!block_add(&dest->body, src->body.instrs[i]))
            return 0;
    }
    (*count)++;
    return 1;
}

/* Rewrite CALL operands in a block: replace short name with qualified name.
   E.g., call "add" -> call "module1:add" */
static void rewrite_calls_in_block(Block *b, const char *short_name,
                                   const char *qualified_name)
{
    for (int i = 0; i < b->count; i++) {
        Instruction *ins = &b->instrs[i];
        if ((ins->opcode == OP_CALL || ins->opcode == OP_ACALL) &&
            ins->op1.kind == OPERAND_CALL_INFO &&
            strcmp(ins->op1.str_val, short_name) == 0)
        {
            strncpy(ins->op1.str_val, qualified_name, OPERAND_STR_MAX - 1);
            ins->op1.str_val[OPERAND_STR_MAX - 1] = '\0';
        }
    }
}

/* Rewrite all call sites in a unit (all procedures, functions, entrypoint) */
static void rewrite_calls_in_unit(ExecutableUnit *unit,
                                  const char *short_name,
                                  const char *qualified_name)
{
    for (int i = 0; i < unit->proc_count; i++)
        rewrite_calls_in_block(&unit->procedures[i].body, short_name, qualified_name);
    for (int i = 0; i < unit->func_count; i++)
        rewrite_calls_in_block(&unit->functions[i].body, short_name, qualified_name);
    rewrite_calls_in_block(&unit->entry_point, short_name, qualified_name);
}

/* Link a compiled unit against its use directives.
   importer_path: filesystem path of the file that was compiled into *main_out */
int magic_link_file(const char *importer_path, CompileResult *main_out)
{
    if (!main_out->success)
        return 0;

    /* Re-parse use directives from the source file */
    char *src = NULL;
    {
        long sz;
        src = read_file(importer_path, &sz);
        if (!src) return 1; /* no source — nothing to link */
    }

    UseDirectiveSet uses;
    memset(&uses, 0, sizeof(uses));

    CompileResult tmp;
    compile_source_internal(src, &tmp, &uses);
    free(src);

    if (!tmp.success || uses.count == 0)
        return main_out->success;

    ExecutableUnit *main_unit = &main_out->unit;

    for (int u = 0; u < uses.count; u++) {
        UseDirective *ud = &uses.items[u];

        /* Resolve module file path */
        char mod_path[USE_PATH_MAX] = {0};
        if (!resolve_module_path(importer_path, ud->module_path,
                                 mod_path, USE_PATH_MAX)) {
            /* Module not found — emit warning but keep going */
            fprintf(stderr, "warning: module '%s' not found (skipping)\n",
                    ud->module_path);
            continue;
        }

        /* Compile the module */
        char *mod_src = NULL;
        {
            long sz2;
            mod_src = read_file(mod_path, &sz2);
        }
        if (!mod_src) {
            fprintf(stderr, "warning: cannot read module '%s' (skipping)\n",
                    mod_path);
            continue;
        }

        CompileResult mod_res;
        compile_source_internal(mod_src, &mod_res, NULL);
        free(mod_src);

        if (!mod_res.success) {
            fprintf(stderr, "warning: module '%s' compile error: %s (skipping)\n",
                    mod_path, mod_res.error);
            continue;
        }

        ExecutableUnit *mod_unit = &mod_res.unit;

        /* Derive the prefix to use for qualified names.
           Use the last segment of the module path (i.e. module file name). */
        char prefix[USE_SIG_NAME_MAX] = {0};
        const char *last_seg = strrchr(ud->module_path, '\\');
        if (last_seg) last_seg++;
        else          last_seg = ud->module_path;
        strncpy(prefix, last_seg, USE_SIG_NAME_MAX - 1);

        /* If the use directive has explicit signatures, import only those */
        if (ud->has_as_block && ud->sig_count > 0) {
            for (int s = 0; s < ud->sig_count; s++) {
                UseSignature *sig = &ud->sigs[s];
                char qual_name[PROC_NAME_MAX];
                snprintf(qual_name, sizeof(qual_name), "%s:%s", prefix, sig->name);

                /* Find matching function/procedure in module */
                if (sig->is_function) {
                    for (int f = 0; f < mod_unit->func_count; f++) {
                        if (str_eq_ci(mod_unit->functions[f].name, sig->name)) {
                            merge_function(main_unit, &mod_unit->functions[f],
                                           qual_name, 1);
                            rewrite_calls_in_unit(main_unit, sig->name, qual_name);
                            break;
                        }
                    }
                } else {
                    for (int pr = 0; pr < mod_unit->proc_count; pr++) {
                        if (str_eq_ci(mod_unit->procedures[pr].name, sig->name)) {
                            merge_function(main_unit, &mod_unit->procedures[pr],
                                           qual_name, 0);
                            rewrite_calls_in_unit(main_unit, sig->name, qual_name);
                            break;
                        }
                    }
                }
            }
        } else {
            /* Import all functions and procedures from the module */
            for (int f = 0; f < mod_unit->func_count; f++) {
                char qual_name[PROC_NAME_MAX];
                snprintf(qual_name, sizeof(qual_name), "%s:%s",
                         prefix, mod_unit->functions[f].name);
                merge_function(main_unit, &mod_unit->functions[f], qual_name, 1);
                rewrite_calls_in_unit(main_unit, mod_unit->functions[f].name, qual_name);
            }
            for (int pr = 0; pr < mod_unit->proc_count; pr++) {
                char qual_name[PROC_NAME_MAX];
                snprintf(qual_name, sizeof(qual_name), "%s:%s",
                         prefix, mod_unit->procedures[pr].name);
                merge_function(main_unit, &mod_unit->procedures[pr], qual_name, 0);
                rewrite_calls_in_unit(main_unit, mod_unit->procedures[pr].name, qual_name);
            }
        }
    }

    return main_out->success;
}

/* ------------------------------------------------------------------ */
/* File I/O                                                            */
/* ------------------------------------------------------------------ */

static char *read_file(const char *path, long *out_size)
{
#if defined(MAGIC_KERNEL_DIAG)
    magic_compile_diag("read_file: fopen");
#endif
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
#if defined(MAGIC_KERNEL_DIAG)
    magic_compile_diag_i2("read_file: size", (int)sz, 0);
#endif
    fseek(f, 0, SEEK_SET);
    char *buf = malloc(sz + 1);
    if (!buf) {
#if defined(MAGIC_KERNEL_DIAG)
        magic_compile_diag("read_file: malloc failed");
#endif
        fclose(f);
        return NULL;
    }
    if (fread(buf, 1, (size_t)sz, f) != (size_t)sz) {
#if defined(MAGIC_KERNEL_DIAG)
        magic_compile_diag("read_file: fread incomplete");
#endif
        free(buf);
        fclose(f);
        return NULL;
    }
    buf[sz] = '\0';
    if (out_size) *out_size = sz;
    fclose(f);
#if defined(MAGIC_KERNEL_DIAG)
    magic_compile_diag("read_file: ok");
#endif
    return buf;
}

int magic_compile_file(const char *path, CompileResult *out)
{
#if defined(MAGIC_KERNEL_DIAG)
    magic_compile_diag("magic_compile_file: enter");
#endif
    long  sz;
    char *src = read_file(path, &sz);
    if (!src) {
        memset(out, 0, sizeof(*out));
        snprintf(out->error, sizeof(out->error), "Cannot open file: %s", path);
        return 0;
    }
#if defined(MAGIC_KERNEL_DIAG)
    magic_compile_diag("magic_compile_file: calling compile_source_internal");
#endif
    UseDirectiveSet uses;
    memset(&uses, 0, sizeof(uses));
    int result = compile_source_internal(src, out, &uses);
    free(src);
#if defined(MAGIC_KERNEL_DIAG)
    magic_compile_diag_i2("magic_compile_file: source done", result, out->success);
#endif
    /* Link use directives if compilation succeeded and uses were found */
    if (result && out->success && uses.count > 0) {
#if defined(MAGIC_KERNEL_DIAG)
        magic_compile_diag("magic_compile_file: linking");
#endif
        result = magic_link_file(path, out);
    }
    return result;
}

/* ------------------------------------------------------------------ */
/* Serialization: text assembly (.agiasm)                              */
/* ------------------------------------------------------------------ */

static const char *opcode_name(Opcode op)
{
    switch (op) {
        case OP_NOP:          return "nop";
        case OP_ADDVERTEX:    return "addvertex";
        case OP_ADDRELATION:  return "addrelation";
        case OP_ADDSHAPE:     return "addshape";
        case OP_CALL:         return "call";
        case OP_PUSH:         return "push";
        case OP_POP:          return "pop";
        case OP_SYSCALL:      return "syscall";
        case OP_RET:          return "ret";
        case OP_MOVE:         return "move";
        case OP_GETVERTEX:    return "getvertex";
        case OP_DEF:          return "def";
        case OP_DEFGEN:       return "defgen";
        case OP_CALLOBJ:      return "callobj";
        case OP_AWAITOBJ:     return "awaitobj";
        case OP_STREAMWAITOBJ:return "streamwaitobj";
        case OP_AWAIT:        return "await";
        case OP_LABEL:        return "label";
        case OP_CMP:          return "cmp";
        case OP_JE:           return "je";
        case OP_JMP:          return "jmp";
        case OP_GETOBJ:       return "getobj";
        case OP_SETOBJ:       return "setobj";
        case OP_STREAMWAIT:   return "streamwait";
        case OP_ACALL:        return "acall";
        case OP_EXPR:         return "expr";
        case OP_DEFEXPR:      return "defexpr";
        case OP_LAMBDA:       return "lambda";
        case OP_EQUALS:       return "equals";
        case OP_NOT:          return "not";
        case OP_LT:           return "lt";
        case OP_ADD:          return "add";
        case OP_SUB:          return "sub";
        case OP_MUL:          return "mul";
        case OP_DIV:          return "div";
        case OP_POW:          return "pow";
        case OP_DEFOBJ:       return "defobj";
        default:              return "nop";
    }
}

static void write_operand(FILE *f, Operand *o)
{
    switch (o->kind) {
        case OPERAND_NONE:          break;
        case OPERAND_INT:           fprintf(f, " %ld", o->int_val); break;
        case OPERAND_FLOAT:         fprintf(f, " %g", o->float_val); break;
        case OPERAND_STRING:        fprintf(f, " \"%s\"", o->str_val); break;
        case OPERAND_MEMORY:        fprintf(f, " [%ld]", o->mem_index); break;
        case OPERAND_GLOBAL_MEMORY: fprintf(f, " global:[%ld]", o->mem_index); break;
        case OPERAND_CALL_INFO:     fprintf(f, " %s", o->str_val); break;
        case OPERAND_LABEL:         fprintf(f, " %s", o->str_val); break;
        case OPERAND_TYPE:          fprintf(f, " %s", o->str_val); break;
        default:                    break;
    }
}

static void write_block(FILE *f, Block *b, const char *prefix)
{
    for (int i = 0; i < b->count; i++) {
        Instruction *ins = &b->instrs[i];
        Opcode opc = ins->opcode;
        fprintf(f, "%s%s", prefix, opcode_name(opc));

        /* Context-sensitive operand output for push instruction */
        if (opc == OP_PUSH && ins->op1.kind == OPERAND_STRING) {
            fprintf(f, " string: \"%s\"", ins->op1.str_val);
        } else if (opc == OP_PUSH && ins->op1.kind == OPERAND_MEMORY) {
            fprintf(f, " [%ld]", ins->op1.mem_index);
        } else if (opc == OP_PUSH && ins->op1.kind == OPERAND_GLOBAL_MEMORY) {
            fprintf(f, " global:[%ld]", ins->op1.mem_index);
        } else {
            write_operand(f, &ins->op1);
        }

        if (ins->op2.kind != OPERAND_NONE)
            write_operand(f, &ins->op2);
        fprintf(f, "\n");
    }
}

int magic_save_agiasm(const ExecutableUnit *unit, const char *path)
{
    FILE *f = fopen(path, "w");
    if (!f) return 0;

    fprintf(f, "@ AGI %s\n", unit->version[0] ? unit->version : "1.0");
    if (unit->name[0])        fprintf(f, "program %s\n", unit->name);
    if (unit->module[0])      fprintf(f, "module %s\n",  unit->module);
    if (unit->system_name[0]) fprintf(f, "system %s\n",  unit->system_name);
    fprintf(f, "\n");

    for (int i = 0; i < unit->proc_count; i++) {
        fprintf(f, "procedure %s\n{\n  asm {\n", unit->procedures[i].name);
        write_block(f, &unit->procedures[i].body, "    ");
        fprintf(f, "  }\n}\n\n");
    }
    for (int i = 0; i < unit->func_count; i++) {
        fprintf(f, "function %s\n{\n  asm {\n", unit->functions[i].name);
        write_block(f, &unit->functions[i].body, "    ");
        fprintf(f, "  }\n}\n\n");
    }

    fprintf(f, "entrypoint\n{\n  asm {\n");
    write_block(f, &((ExecutableUnit *)unit)->entry_point, "    ");
    fprintf(f, "  }\n}\n");

    if (fclose(f) != 0)
        return 0;
    return 1;
}

/* ------------------------------------------------------------------ */
/* Serialization: binary (.agic)                                       */
/* Simple binary format:                                               */
/*   Magic: "AGIC" (4 bytes)                                           */
/*   Version string (null-terminated)                                  */
/*   Name, Module, System strings (null-terminated)                    */
/*   Procedure count (uint32)                                          */
/*   For each procedure: name + block                                  */
/*   Function count (uint32)                                           */
/*   For each function: name + block                                   */
/*   Entry point block                                                 */
/*                                                                     */
/*   Block format:                                                      */
/*     instruction count (uint32)                                       */
/*     For each instruction: opcode(uint8) op1_kind(uint8) op1_data    */
/*                           op2_kind(uint8) op2_data                  */
/* ------------------------------------------------------------------ */

static void write_str(FILE *f, const char *s)
{
    fputs(s, f);
    fputc(0, f);
}

static void write_u32(FILE *f, uint32_t v)
{
    fwrite(&v, 4, 1, f);
}

static void write_u8(FILE *f, uint8_t v)
{
    fwrite(&v, 1, 1, f);
}

static void write_i64(FILE *f, int64_t v)
{
    fwrite(&v, 8, 1, f);
}

static void write_f64(FILE *f, double v)
{
    fwrite(&v, 8, 1, f);
}

static void write_operand_bin(FILE *f, Operand *o)
{
    write_u8(f, (uint8_t)o->kind);
    switch (o->kind) {
        case OPERAND_NONE: break;
        case OPERAND_INT:  write_i64(f, (int64_t)o->int_val); break;
        case OPERAND_FLOAT:write_f64(f, o->float_val); break;
        case OPERAND_STRING:
        case OPERAND_CALL_INFO:
        case OPERAND_LABEL:
        case OPERAND_TYPE:
            write_str(f, o->str_val); break;
        case OPERAND_MEMORY:
        case OPERAND_GLOBAL_MEMORY:
            write_i64(f, (int64_t)o->mem_index);
            write_u8(f, (uint8_t)o->is_global);
            break;
        default: break;
    }
}

static void write_block_bin(FILE *f, Block *b)
{
    write_u32(f, (uint32_t)b->count);
    for (int i = 0; i < b->count; i++) {
        write_u8(f, (uint8_t)b->instrs[i].opcode);
        write_operand_bin(f, &b->instrs[i].op1);
        write_operand_bin(f, &b->instrs[i].op2);
    }
}

int magic_save_agic(const ExecutableUnit *unit, const char *path)
{
    FILE *f = fopen(path, "wb");
    if (!f) return 0;

    fwrite("AGIC", 4, 1, f);
    write_str(f, unit->version);
    write_str(f, unit->name);
    write_str(f, unit->module);
    write_str(f, unit->system_name);

    write_u32(f, (uint32_t)unit->proc_count);
    for (int i = 0; i < unit->proc_count; i++) {
        write_str(f, unit->procedures[i].name);
        write_block_bin(f, &unit->procedures[i].body);
    }

    write_u32(f, (uint32_t)unit->func_count);
    for (int i = 0; i < unit->func_count; i++) {
        write_str(f, unit->functions[i].name);
        write_block_bin(f, &unit->functions[i].body);
    }

    write_block_bin(f, &((ExecutableUnit *)unit)->entry_point);

    if (fclose(f) != 0)
        return 0;
    return 1;
}

/* ------------------------------------------------------------------ */
/* Load binary (.agic)                                                 */
/* ------------------------------------------------------------------ */

static int read_str(FILE *f, char *buf, int buflen)
{
    int i = 0;
    int c;
    while ((c = fgetc(f)) != EOF && c != 0 && i < buflen - 1)
        buf[i++] = (char)c;
    buf[i] = '\0';
    return (c != EOF || i > 0);
}

static uint32_t read_u32(FILE *f)
{
    uint32_t v = 0;
    fread(&v, 4, 1, f);
    return v;
}

static uint8_t read_u8(FILE *f)
{
    uint8_t v = 0;
    fread(&v, 1, 1, f);
    return v;
}

static int64_t read_i64(FILE *f)
{
    int64_t v = 0;
    fread(&v, 8, 1, f);
    return v;
}

static double read_f64(FILE *f)
{
    double v = 0;
    fread(&v, 8, 1, f);
    return v;
}

static Operand read_operand_bin(FILE *f)
{
    Operand o = op_none();
    uint8_t kind = read_u8(f);
    o.kind = (OperandKind)kind;
    switch (o.kind) {
        case OPERAND_NONE: break;
        case OPERAND_INT:  o.int_val = (long)read_i64(f); break;
        case OPERAND_FLOAT:o.float_val = read_f64(f); break;
        case OPERAND_STRING:
        case OPERAND_CALL_INFO:
        case OPERAND_LABEL:
        case OPERAND_TYPE:
            read_str(f, o.str_val, OPERAND_STR_MAX); break;
        case OPERAND_MEMORY:
        case OPERAND_GLOBAL_MEMORY:
            o.mem_index = (long)read_i64(f);
            o.is_global = read_u8(f);
            break;
        default: break;
    }
    return o;
}

static int read_block_bin(FILE *f, Block *b)
{
    block_init(b);
    uint32_t count = read_u32(f);
    for (uint32_t i = 0; i < count; i++) {
        Instruction instr;
        instr.opcode = (Opcode)read_u8(f);
        instr.op1    = read_operand_bin(f);
        instr.op2    = read_operand_bin(f);
        block_add(b, instr);
    }
    return 1;
}

typedef struct {
    const unsigned char *b;
    size_t len;
    size_t pos;
} MagicAgicBuf;

static int magic_buf_read_str(MagicAgicBuf *m, char *buf, int buflen)
{
    int i = 0;
    while (m->pos < m->len && i < buflen - 1) {
        unsigned char c = m->b[m->pos++];
        if (c == 0)
            break;
        buf[i++] = (char)c;
    }
    buf[i] = '\0';
    return 1;
}

static uint32_t magic_buf_read_u32(MagicAgicBuf *m)
{
    uint32_t v = 0;
    if (m->pos + 4 > m->len)
        return 0;
    memcpy(&v, m->b + m->pos, 4);
    m->pos += 4;
    return v;
}

static uint8_t magic_buf_read_u8(MagicAgicBuf *m)
{
    if (m->pos >= m->len)
        return 0;
    return m->b[m->pos++];
}

static int64_t magic_buf_read_i64(MagicAgicBuf *m)
{
    int64_t v = 0;
    if (m->pos + 8 > m->len)
        return 0;
    memcpy(&v, m->b + m->pos, 8);
    m->pos += 8;
    return v;
}

static double magic_buf_read_f64(MagicAgicBuf *m)
{
    double v = 0;
    if (m->pos + 8 > m->len)
        return 0;
    memcpy(&v, m->b + m->pos, 8);
    m->pos += 8;
    return v;
}

static Operand magic_buf_read_operand_bin(MagicAgicBuf *m)
{
    Operand o = op_none();
    uint8_t kind = magic_buf_read_u8(m);
    o.kind = (OperandKind)kind;
    switch (o.kind) {
        case OPERAND_NONE: break;
        case OPERAND_INT:  o.int_val = (long)magic_buf_read_i64(m); break;
        case OPERAND_FLOAT:o.float_val = magic_buf_read_f64(m); break;
        case OPERAND_STRING:
        case OPERAND_CALL_INFO:
        case OPERAND_LABEL:
        case OPERAND_TYPE:
            magic_buf_read_str(m, o.str_val, OPERAND_STR_MAX); break;
        case OPERAND_MEMORY:
        case OPERAND_GLOBAL_MEMORY:
            o.mem_index = (long)magic_buf_read_i64(m);
            o.is_global = magic_buf_read_u8(m);
            break;
        default: break;
    }
    return o;
}

static int magic_buf_read_block_bin(MagicAgicBuf *m, Block *b)
{
    block_init(b);
    uint32_t count = magic_buf_read_u32(m);
    for (uint32_t i = 0; i < count; i++) {
        Instruction instr;
        instr.opcode = (Opcode)magic_buf_read_u8(m);
        instr.op1    = magic_buf_read_operand_bin(m);
        instr.op2    = magic_buf_read_operand_bin(m);
        block_add(b, instr);
    }
    return 1;
}

int magic_load_agic_from_buffer(const unsigned char *data, size_t len,
                                ExecutableUnit *unit)
{
    if (!data || len < 4)
        return 0;
    if (data[0] != 'A' || data[1] != 'G' || data[2] != 'I' || data[3] != 'C')
        return 0;

    MagicAgicBuf m;
    m.b = data;
    m.len = len;
    m.pos = 4;

    memset(unit, 0, sizeof(*unit));
    magic_buf_read_str(&m, unit->version, NAME_MAX_LEN);
    magic_buf_read_str(&m, unit->name, NAME_MAX_LEN);
    magic_buf_read_str(&m, unit->module, NAME_MAX_LEN);
    magic_buf_read_str(&m, unit->system_name, NAME_MAX_LEN);

    uint32_t proc_count = magic_buf_read_u32(&m);
    for (uint32_t i = 0; i < proc_count && i < MAX_PROCS; i++) {
        magic_buf_read_str(&m, unit->procedures[i].name, PROC_NAME_MAX);
        magic_buf_read_block_bin(&m, &unit->procedures[i].body);
        unit->proc_count++;
    }

    uint32_t func_count = magic_buf_read_u32(&m);
    for (uint32_t i = 0; i < func_count && i < MAX_PROCS; i++) {
        magic_buf_read_str(&m, unit->functions[i].name, PROC_NAME_MAX);
        magic_buf_read_block_bin(&m, &unit->functions[i].body);
        unit->func_count++;
    }

    magic_buf_read_block_bin(&m, &unit->entry_point);
    return 1;
}

int magic_load_agic(const char *path, ExecutableUnit *unit)
{
    FILE *f = fopen(path, "rb");
    if (!f) return 0;

    char magic[5] = {0};
    if (fread(magic, 4, 1, f) != 1 || strncmp(magic, "AGIC", 4) != 0) {
        fclose(f);
        return 0;
    }

    memset(unit, 0, sizeof(*unit));
    read_str(f, unit->version,     NAME_MAX_LEN);
    read_str(f, unit->name,        NAME_MAX_LEN);
    read_str(f, unit->module,      NAME_MAX_LEN);
    read_str(f, unit->system_name, NAME_MAX_LEN);

    uint32_t proc_count = read_u32(f);
    for (uint32_t i = 0; i < proc_count && i < MAX_PROCS; i++) {
        read_str(f, unit->procedures[i].name, PROC_NAME_MAX);
        read_block_bin(f, &unit->procedures[i].body);
        unit->proc_count++;
    }

    uint32_t func_count = read_u32(f);
    for (uint32_t i = 0; i < func_count && i < MAX_PROCS; i++) {
        read_str(f, unit->functions[i].name, PROC_NAME_MAX);
        read_block_bin(f, &unit->functions[i].body);
        unit->func_count++;
    }

    read_block_bin(f, &unit->entry_point);

    fclose(f);
    return 1;
}

/* ------------------------------------------------------------------ */
/* Utility: line/col from source position                              */
/* ------------------------------------------------------------------ */

void magic_get_line_col(const char *source, int pos, int *line, int *col)
{
    *line = 1; *col = 1;
    if (!source || pos < 0) return;
    for (int i = 0; i < pos && source[i]; i++) {
        if (source[i] == '\n') { (*line)++; *col = 1; }
        else                   { (*col)++; }
    }
}

/* ------------------------------------------------------------------ */
/* ExecutableUnit field alias: system_name                             */
/* (C doesn't allow 'system' as a field name on some platforms since  */
/*  system() is a stdlib function - use system_name throughout)        */
/* ------------------------------------------------------------------ */
