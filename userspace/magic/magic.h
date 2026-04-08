/*
 * magic.h - Magic Language Kernel C Port
 *
 * A C port of the Magic language compiler and interpreter from
 * Magic_Kernel_Dotnet. Provides the spc (compiler) and spe (emulator)
 * commands for the Space OS terminal.
 *
 * Magic language (.agi files) features:
 *   - Stack-based virtual machine
 *   - AGI bytecode format (.agic binary, .agiasm text)
 *   - Opcodes: nop, push, pop, call, ret, cmp, je, jmp, label,
 *              def, defgen, callobj, awaitobj, await, streamwait,
 *              getobj, setobj, addvertex, addrelation, addshape
 *   - Program structure: @AGI version, program, module, system,
 *                        procedure, function, entrypoint
 */

#ifndef MAGIC_H
#define MAGIC_H

#include <stdint.h>
#include <stddef.h>

/* ------------------------------------------------------------------ */
/* Token kinds                                                         */
/* ------------------------------------------------------------------ */
typedef enum {
    TOK_EOF = 0,
    TOK_IDENTIFIER,
    TOK_NUMBER,
    TOK_FLOAT,
    TOK_STRING,
    TOK_NEWLINE,
    TOK_COLON,
    TOK_COMMA,
    TOK_LBRACKET,
    TOK_RBRACKET,
    TOK_LPAREN,
    TOK_RPAREN,
    TOK_LBRACE,
    TOK_RBRACE,
    TOK_DOT,
    TOK_ASSIGN,
    TOK_LESS,
    TOK_GREATER,
    TOK_SEMICOLON,
} TokenKind;

#define TOKEN_VALUE_MAX 512

typedef struct {
    TokenKind kind;
    char      value[TOKEN_VALUE_MAX];
    int       start;
    int       end;
} Token;

/* ------------------------------------------------------------------ */
/* Opcodes - matches Magic.Kernel/Processor/Opcodes.cs enum values     */
/* ------------------------------------------------------------------ */
typedef enum {
    OP_NOP           = 0,
    OP_ADDVERTEX     = 1,
    OP_ADDRELATION   = 2,
    OP_ADDSHAPE      = 3,
    OP_CALL          = 4,
    OP_PUSH          = 5,
    OP_POP           = 6,
    OP_SYSCALL       = 7,
    OP_RET           = 8,
    OP_MOVE          = 9,
    OP_GETVERTEX     = 10,
    OP_DEF           = 11,
    OP_DEFGEN        = 12,
    OP_CALLOBJ       = 13,
    OP_AWAITOBJ      = 14,
    OP_STREAMWAITOBJ = 15,
    OP_AWAIT         = 16,
    OP_LABEL         = 17,
    OP_CMP           = 18,
    OP_JE            = 19,
    OP_JMP           = 20,
    OP_GETOBJ        = 21,
    OP_SETOBJ        = 22,
    OP_STREAMWAIT    = 23,
    OP_ACALL         = 24,  /* async call */
    OP_EXPR          = 25,  /* start lambda body collection */
    OP_DEFEXPR       = 26,  /* end lambda body / lambda return */
    OP_LAMBDA        = 27,  /* no-op at runtime; marks lambda body boundary */
    OP_EQUALS        = 28,  /* pop b, pop a, push (a == b) */
    OP_NOT           = 29,  /* pop v, push !v */
    OP_LT            = 30,  /* pop b, pop a, push (a < b) */
    OP_ADD           = 31,  /* pop b, pop a, push (a + b) */
    OP_SUB           = 32,  /* pop b, pop a, push (a - b) */
    OP_MUL           = 33,  /* pop b, pop a, push (a * b) */
    OP_DIV           = 34,  /* pop b, pop a, push (a / b) */
    OP_POW           = 35,  /* pop b, pop a, push (a ^ b) */
    OP_DEFOBJ        = 36,  /* object construction (semantic alias of OP_DEF) */
} Opcode;

/* ------------------------------------------------------------------ */
/* Operand types                                                       */
/* ------------------------------------------------------------------ */
typedef enum {
    OPERAND_NONE = 0,
    OPERAND_INT,
    OPERAND_FLOAT,
    OPERAND_STRING,
    OPERAND_MEMORY,         /* [index] */
    OPERAND_GLOBAL_MEMORY,  /* global:[index] */
    OPERAND_CALL_INFO,      /* function name + params */
    OPERAND_LABEL,          /* label name */
    OPERAND_TYPE,           /* type literal */
    OPERAND_VERTEX,
    OPERAND_RELATION,
    OPERAND_SHAPE,
} OperandKind;

#define OPERAND_STR_MAX 512

typedef struct CallParam {
    char              name[OPERAND_STR_MAX];
    char              value[OPERAND_STR_MAX];
    long              index;
    struct CallParam *next;
} CallParam;

typedef struct {
    OperandKind kind;
    long        int_val;
    double      float_val;
    char        str_val[OPERAND_STR_MAX];
    long        mem_index;
    int         is_global;
    CallParam  *params;     /* for OPERAND_CALL_INFO */
} Operand;

/* ------------------------------------------------------------------ */
/* Instruction                                                         */
/* ------------------------------------------------------------------ */
typedef struct Instruction {
    Opcode   opcode;
    Operand  op1;
    Operand  op2;
} Instruction;

/* ------------------------------------------------------------------ */
/* Execution block (list of instructions)                              */
/* ------------------------------------------------------------------ */
#define BLOCK_INIT_CAP 64

typedef struct {
    Instruction *instrs;
    int          count;
    int          cap;
} Block;

/* ------------------------------------------------------------------ */
/* Procedure / Function                                                */
/* ------------------------------------------------------------------ */
#define PROC_NAME_MAX 256
#define MAX_PROCS 256

typedef struct {
    char  name[PROC_NAME_MAX];
    Block body;
} Procedure;

/* ------------------------------------------------------------------ */
/* Executable unit                                                     */
/* ------------------------------------------------------------------ */
#define NAME_MAX_LEN 256

typedef struct {
    char       version[NAME_MAX_LEN];
    char       name[NAME_MAX_LEN];
    char       module[NAME_MAX_LEN];
    char       system_name[NAME_MAX_LEN];  /* 'system' is reserved on some platforms */
    char       space_name[NAME_MAX_LEN * 3];
    Block      entry_point;
    Procedure  procedures[MAX_PROCS];
    int        proc_count;
    Procedure  functions[MAX_PROCS];
    int        func_count;
} ExecutableUnit;

/* ------------------------------------------------------------------ */
/* Value (runtime)                                                     */
/* ------------------------------------------------------------------ */
typedef enum {
    VAL_NIL = 0,
    VAL_INT,
    VAL_FLOAT,
    VAL_STRING,
    VAL_BOOL,
} ValueKind;

#define VAL_STR_MAX 1024

typedef struct {
    ValueKind kind;
    long      int_val;
    double    float_val;
    char      str_val[VAL_STR_MAX];
    int       bool_val;
} Value;

/* ------------------------------------------------------------------ */
/* Runtime stack                                                       */
/* ------------------------------------------------------------------ */
#define STACK_MAX 256

typedef struct {
    Value items[STACK_MAX];
    int   top;
} Stack;

/* ------------------------------------------------------------------ */
/* Memory (indexed slots)                                              */
/* ------------------------------------------------------------------ */
#define MEM_MAX 256

typedef struct {
    long  index;
    Value val;
    int   used;
} MemSlot;

typedef struct {
    MemSlot slots[MEM_MAX];
    int     count;
} Memory;

/* ------------------------------------------------------------------ */
/* Call frame                                                          */
/* ------------------------------------------------------------------ */
#define CALLSTACK_MAX 64

typedef struct {
    Block *block;
    int    ret_ip;
    char   name[PROC_NAME_MAX];
} CallFrame;

typedef struct {
    CallFrame frames[CALLSTACK_MAX];
    int       top;
} CallStack;

/* ------------------------------------------------------------------ */
/* Label map                                                           */
/* ------------------------------------------------------------------ */
#define LABEL_MAX 256

typedef struct {
    char name[PROC_NAME_MAX];
    int  offset;
} Label;

typedef struct {
    Label labels[LABEL_MAX];
    int   count;
} LabelMap;

/* ------------------------------------------------------------------ */
/* Interpreter state                                                   */
/* ------------------------------------------------------------------ */
typedef struct {
    ExecutableUnit *unit;
    Block          *current_block;
    int             ip;
    Stack           stack;
    Memory          memory;
    Memory          global_memory;
    CallStack       call_stack;
    LabelMap        label_map;
    int             flags;   /* comparison flags */
    int             verbose; /* debug/trace mode */
} Interpreter;

/* ------------------------------------------------------------------ */
/* Scanner                                                             */
/* ------------------------------------------------------------------ */
typedef struct {
    const char *src;
    int         src_len;
    int         pos;
    Token      *tokens;
    int         tok_count;
    int         tok_cap;
    int         tok_pos;
} Scanner;

/* ------------------------------------------------------------------ */
/* Compilation result                                                  */
/* ------------------------------------------------------------------ */
typedef struct {
    int            success;
    char           error[1024];
    int            error_line;
    int            error_col;
    ExecutableUnit unit;
} CompileResult;

/* ------------------------------------------------------------------ */
/* API                                                                 */
/* ------------------------------------------------------------------ */

/* Scanner */
int     scanner_init(Scanner *s, const char *src);
void    scanner_free(Scanner *s);
Token   scanner_scan(Scanner *s);
Token   scanner_current(Scanner *s);
Token  *scanner_watch(Scanner *s, int offset);
int     scanner_save(Scanner *s);
void    scanner_restore(Scanner *s, int pos);

/* Compiler */
int     magic_compile_source(const char *source, CompileResult *out);
int     magic_compile_file(const char *path, CompileResult *out);
int     magic_link_file(const char *importer_path, CompileResult *main_out);

/* Serialization */
int     magic_save_agic(const ExecutableUnit *unit, const char *path);
int     magic_load_agic(const char *path, ExecutableUnit *unit);
int     magic_load_agic_from_buffer(const unsigned char *data, size_t len,
                                    ExecutableUnit *unit);
int     magic_save_agiasm(const ExecutableUnit *unit, const char *path);

/* Interpreter */
void    interpreter_init(Interpreter *interp);
int     interpreter_run(Interpreter *interp, ExecutableUnit *unit);

/* Block helpers */
void    block_init(Block *b);
void    block_free(Block *b);
int     block_add(Block *b, Instruction instr);

/* Memory helpers */
void    memory_init(Memory *m);
int     memory_set(Memory *m, long index, Value v);
int     memory_get(Memory *m, long index, Value *out);

/* Stack helpers */
void    stack_init(Stack *s);
int     stack_push(Stack *s, Value v);
int     stack_pop(Stack *s, Value *out);
int     stack_peek(Stack *s, Value *out);

/* Value helpers */
Value   val_nil(void);
Value   val_int(long n);
Value   val_float(double f);
Value   val_str(const char *s);
Value   val_bool(int b);
int     val_truthy(Value v);
void    val_to_str(Value v, char *buf, int buflen);
int     val_equal(Value a, Value b);

/* Utilities */
void    magic_get_line_col(const char *source, int pos, int *line, int *col);

#if defined(MAGIC_KERNEL_DIAG)
/* UART diagnostics when Magic is built into kernel (magic_kern.c) */
void magic_compile_diag(const char *phase);
void magic_compile_diag_i2(const char *tag, int a, int b);
#endif

#endif /* MAGIC_H */
