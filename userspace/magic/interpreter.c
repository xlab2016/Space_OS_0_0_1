/*
 * interpreter.c - Magic Language Interpreter
 *
 * Executes an ExecutableUnit (Magic bytecode).
 * Faithful C port of Magic.Kernel/Interpretation/Interpreter.cs
 * and Magic.Kernel/Interpretation/SystemFunctions.cs
 */

#include "magic.h"
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>
#include <math.h>

/* ------------------------------------------------------------------ */
/* Value helpers                                                       */
/* ------------------------------------------------------------------ */

Value val_nil(void)
{
    Value v; memset(&v, 0, sizeof(v)); v.kind = VAL_NIL; return v;
}

Value val_int(long n)
{
    Value v = val_nil(); v.kind = VAL_INT; v.int_val = n; return v;
}

Value val_float(double f)
{
    Value v = val_nil(); v.kind = VAL_FLOAT; v.float_val = f; return v;
}

Value val_str(const char *s)
{
    Value v = val_nil();
    v.kind = VAL_STRING;
    strncpy(v.str_val, s ? s : "", VAL_STR_MAX - 1);
    return v;
}

Value val_bool(int b)
{
    Value v = val_nil(); v.kind = VAL_BOOL; v.bool_val = b ? 1 : 0;
    return v;
}

int val_truthy(Value v)
{
    switch (v.kind) {
        case VAL_NIL:   return 0;
        case VAL_BOOL:  return v.bool_val != 0;
        case VAL_INT:   return v.int_val != 0;
        case VAL_FLOAT: return v.float_val != 0.0;
        case VAL_STRING:
            return strlen(v.str_val) > 0 &&
                   (strcmp(v.str_val, "true") == 0 ||
                    strcmp(v.str_val, "1") == 0);
    }
    return 0;
}

void val_to_str(Value v, char *buf, int buflen)
{
    switch (v.kind) {
        case VAL_NIL:    snprintf(buf, buflen, "(nil)"); break;
        case VAL_BOOL:   snprintf(buf, buflen, "%s", v.bool_val ? "true" : "false"); break;
        case VAL_INT:    snprintf(buf, buflen, "%ld", v.int_val); break;
        case VAL_FLOAT:  snprintf(buf, buflen, "%g", v.float_val); break;
        case VAL_STRING: snprintf(buf, buflen, "%s", v.str_val); break;
    }
}

int val_equal(Value a, Value b)
{
    /* Try numeric comparison first */
    double da = 0, db = 0;
    int    a_num = 0, b_num = 0;
    if (a.kind == VAL_INT)   { da = (double)a.int_val;   a_num = 1; }
    if (a.kind == VAL_FLOAT) { da = a.float_val;          a_num = 1; }
    if (b.kind == VAL_INT)   { db = (double)b.int_val;   b_num = 1; }
    if (b.kind == VAL_FLOAT) { db = b.float_val;          b_num = 1; }
    if (a_num && b_num)
        return fabs(da - db) < 1e-12;

    if (a.kind == VAL_STRING && b.kind == VAL_STRING)
        return strcmp(a.str_val, b.str_val) == 0;
    if (a.kind == VAL_BOOL && b.kind == VAL_BOOL)
        return a.bool_val == b.bool_val;
    if (a.kind == VAL_NIL && b.kind == VAL_NIL)
        return 1;
    return 0;
}

/* ------------------------------------------------------------------ */
/* Stack helpers                                                       */
/* ------------------------------------------------------------------ */

void stack_init(Stack *s)
{
    s->top = 0;
}

int stack_push(Stack *s, Value v)
{
    if (s->top >= STACK_MAX) {
        fprintf(stderr, "[interp] stack overflow\n");
        return 0;
    }
    s->items[s->top++] = v;
    return 1;
}

int stack_pop(Stack *s, Value *out)
{
    if (s->top <= 0) {
        if (out) *out = val_nil();
        return 0;
    }
    if (out) *out = s->items[--s->top];
    else     s->top--;
    return 1;
}

int stack_peek(Stack *s, Value *out)
{
    if (s->top <= 0) { if (out) *out = val_nil(); return 0; }
    if (out) *out = s->items[s->top - 1];
    return 1;
}

/* ------------------------------------------------------------------ */
/* Memory helpers                                                      */
/* ------------------------------------------------------------------ */

void memory_init(Memory *m)
{
    memset(m, 0, sizeof(*m));
}

int memory_set(Memory *m, long index, Value v)
{
    /* Search for existing slot */
    for (int i = 0; i < m->count; i++) {
        if (m->slots[i].used && m->slots[i].index == index) {
            m->slots[i].val = v;
            return 1;
        }
    }
    /* Find empty slot */
    for (int i = 0; i < MEM_MAX; i++) {
        if (!m->slots[i].used) {
            m->slots[i].index = index;
            m->slots[i].val   = v;
            m->slots[i].used  = 1;
            if (i >= m->count) m->count = i + 1;
            return 1;
        }
    }
    fprintf(stderr, "[interp] memory full\n");
    return 0;
}

int memory_get(Memory *m, long index, Value *out)
{
    for (int i = 0; i < m->count; i++) {
        if (m->slots[i].used && m->slots[i].index == index) {
            if (out) *out = m->slots[i].val;
            return 1;
        }
    }
    if (out) *out = val_nil();
    return 0;
}

/* ------------------------------------------------------------------ */
/* Label map                                                           */
/* ------------------------------------------------------------------ */

static void build_label_map(LabelMap *lm, Block *b)
{
    lm->count = 0;
    if (!b) return;
    for (int i = 0; i < b->count && lm->count < LABEL_MAX; i++) {
        if (b->instrs[i].opcode == OP_LABEL &&
            b->instrs[i].op1.kind == OPERAND_LABEL)
        {
            strncpy(lm->labels[lm->count].name,
                    b->instrs[i].op1.str_val, PROC_NAME_MAX - 1);
            lm->labels[lm->count].offset = i + 1; /* after the label */
            lm->count++;
        }
    }
}

static int resolve_label(LabelMap *lm, const char *name)
{
    for (int i = 0; i < lm->count; i++)
        if (strcmp(lm->labels[i].name, name) == 0)
            return lm->labels[i].offset;
    return -1;
}

/* ------------------------------------------------------------------ */
/* Find procedure/function in unit                                     */
/* ------------------------------------------------------------------ */

static Block *find_procedure(ExecutableUnit *unit, const char *name)
{
    for (int i = 0; i < unit->proc_count; i++)
        if (strcmp(unit->procedures[i].name, name) == 0)
            return &unit->procedures[i].body;
    return NULL;
}

static Block *find_function(ExecutableUnit *unit, const char *name)
{
    for (int i = 0; i < unit->func_count; i++)
        if (strcmp(unit->functions[i].name, name) == 0)
            return &unit->functions[i].body;
    return NULL;
}

/* ------------------------------------------------------------------ */
/* System functions (mirrors SystemFunctions.cs)                      */
/* ------------------------------------------------------------------ */

static int exec_syscall(Interpreter *interp, const char *fn_name)
{
    /* ---- print ---- */
    if (strcmp(fn_name, "print") == 0) {
        /* Stack: [..., arg1, ..., argN, arity] */
        Value arity_val;
        stack_pop(&interp->stack, &arity_val);
        long arity = (arity_val.kind == VAL_INT) ? arity_val.int_val : 0;

        /* Collect args (they are in order arg1..argN) */
        Value args[64];
        int   n = (arity > 64) ? 64 : (int)arity;
        for (int i = n - 1; i >= 0; i--) {
            stack_pop(&interp->stack, &args[i]);
        }
        for (int i = 0; i < n; i++) {
            char buf[VAL_STR_MAX];
            val_to_str(args[i], buf, sizeof(buf));
            printf("%s", buf);
        }
        if (n == 0) printf("\n");
        else        printf("\n");
        fflush(stdout);
        return 1;
    }

    /* ---- debug / debugger ---- */
    if (strcmp(fn_name, "debug") == 0 || strcmp(fn_name, "debugger") == 0) {
        /* Print top of stack */
        Value v;
        if (stack_peek(&interp->stack, &v)) {
            char buf[VAL_STR_MAX];
            val_to_str(v, buf, sizeof(buf));
            fprintf(stderr, "[debug] %s\n", buf);
        }
        return 1;
    }

    /* ---- origin ---- */
    if (strcmp(fn_name, "origin") == 0) {
        /* push space name */
        stack_push(&interp->stack,
                   val_str(interp->unit ? interp->unit->space_name : ""));
        return 1;
    }

    /* ---- convert ---- */
    if (strcmp(fn_name, "convert") == 0) {
        /* Stack: value, type */
        Value type_val, src_val;
        stack_pop(&interp->stack, &type_val);
        stack_pop(&interp->stack, &src_val);
        char type_str[OPERAND_STR_MAX];
        val_to_str(type_val, type_str, sizeof(type_str));

        Value result = src_val;
        if (strcmp(type_str, "string") == 0) {
            char buf[VAL_STR_MAX];
            val_to_str(src_val, buf, sizeof(buf));
            result = val_str(buf);
        } else if (strcmp(type_str, "int") == 0 || strcmp(type_str, "long") == 0) {
            if (src_val.kind == VAL_STRING) result = val_int(atol(src_val.str_val));
            else if (src_val.kind == VAL_FLOAT) result = val_int((long)src_val.float_val);
            else if (src_val.kind == VAL_BOOL)  result = val_int(src_val.bool_val ? 1 : 0);
        } else if (strcmp(type_str, "float") == 0 || strcmp(type_str, "double") == 0) {
            if (src_val.kind == VAL_STRING) result = val_float(atof(src_val.str_val));
            else if (src_val.kind == VAL_INT)  result = val_float((double)src_val.int_val);
        } else if (strcmp(type_str, "bool") == 0) {
            result = val_bool(val_truthy(src_val));
        }
        stack_push(&interp->stack, result);
        return 1;
    }

    /* ---- get ---- */
    if (strcmp(fn_name, "get") == 0) {
        /* Basic get: return nil for now since we don't have a disk */
        stack_push(&interp->stack, val_nil());
        return 1;
    }

    /* ---- compile ---- */
    if (strcmp(fn_name, "compile") == 0) {
        /* Stack: source_code */
        Value src_val;
        stack_pop(&interp->stack, &src_val);
        char src_str[VAL_STR_MAX];
        val_to_str(src_val, src_str, sizeof(src_str));

        CompileResult cr;
        int ok = magic_compile_source(src_str, &cr);
        stack_push(&interp->stack, val_bool(ok && cr.success));
        return 1;
    }

    /* ---- intersect ---- */
    if (strcmp(fn_name, "intersect") == 0) {
        /* Return empty set */
        stack_push(&interp->stack, val_nil());
        return 1;
    }

    return 0; /* not handled */
}

/* ------------------------------------------------------------------ */
/* Execute one instruction                                             */
/* ------------------------------------------------------------------ */

static int exec_one(Interpreter *interp)
{
    if (!interp->current_block) return 0;
    if (interp->ip >= interp->current_block->count) return 0;

    Instruction *cmd = &interp->current_block->instrs[interp->ip++];

    if (interp->verbose) {
        fprintf(stderr, "[trace] ip=%d opcode=%d\n", interp->ip - 1, cmd->opcode);
    }

    switch (cmd->opcode) {
    case OP_NOP:
        break;

    case OP_PUSH: {
        Value v = val_nil();
        switch (cmd->op1.kind) {
            case OPERAND_INT:   v = val_int(cmd->op1.int_val); break;
            case OPERAND_FLOAT: v = val_float(cmd->op1.float_val); break;
            case OPERAND_STRING:v = val_str(cmd->op1.str_val); break;
            case OPERAND_TYPE:  v = val_str(cmd->op1.str_val); break;
            case OPERAND_MEMORY:
            case OPERAND_GLOBAL_MEMORY: {
                /* ResolveMemory: use global when no active call frame */
                int use_global = (cmd->op1.is_global ||
                                  cmd->op1.kind == OPERAND_GLOBAL_MEMORY ||
                                  interp->call_stack.top == 0);
                Memory *mem = use_global ? &interp->global_memory : &interp->memory;
                Value fetched;
                if (!memory_get(mem, cmd->op1.mem_index, &fetched)) {
                    /* Fallback: try the other region */
                    Memory *other = use_global ? &interp->memory : &interp->global_memory;
                    if (!memory_get(other, cmd->op1.mem_index, &fetched)) {
                        if (interp->verbose)
                            fprintf(stderr, "[interp] push: memory[%ld] not set\n",
                                    cmd->op1.mem_index);
                        fetched = val_nil();
                    }
                }
                v = fetched;
                break;
            }
            default: break;
        }
        stack_push(&interp->stack, v);
        break;
    }

    case OP_POP: {
        if (interp->stack.top == 0)
            break; /* nothing to pop */

        if (cmd->op1.kind == OPERAND_NONE) {
            stack_pop(&interp->stack, NULL);
        } else {
            long idx = cmd->op1.mem_index;
            Value top_val;
            stack_pop(&interp->stack, &top_val);
            /* ResolveMemory: global if no call frame active or explicitly global */
            int use_global = (cmd->op1.is_global ||
                              cmd->op1.kind == OPERAND_GLOBAL_MEMORY ||
                              interp->call_stack.top == 0);
            Memory *mem = use_global ? &interp->global_memory : &interp->memory;
            memory_set(mem, idx, top_val);
        }
        break;
    }

    case OP_CALL:
    case OP_SYSCALL: {
        const char *fn_name = cmd->op1.str_val;

        /* Try user-defined procedure */
        if (cmd->opcode == OP_CALL && interp->unit) {
            Block *block = find_procedure(interp->unit, fn_name);
            if (!block)
                block = find_function(interp->unit, fn_name);

            if (block) {
                /* Push call frame */
                if (interp->call_stack.top >= CALLSTACK_MAX) {
                    fprintf(stderr, "[interp] call stack overflow\n");
                    return -1;
                }
                CallFrame *frame = &interp->call_stack.frames[interp->call_stack.top++];
                frame->block  = interp->current_block;
                frame->ret_ip = interp->ip;
                strncpy(frame->name, fn_name, PROC_NAME_MAX - 1);

                interp->current_block = block;
                build_label_map(&interp->label_map, block);
                interp->ip = 0;
                break;
            }

            /* Check label in current block */
            int label_offset = resolve_label(&interp->label_map, fn_name);
            if (label_offset >= 0) {
                CallFrame *frame = &interp->call_stack.frames[interp->call_stack.top++];
                frame->block  = interp->current_block;
                frame->ret_ip = interp->ip;
                strncpy(frame->name, fn_name, PROC_NAME_MAX - 1);
                interp->ip = label_offset;
                break;
            }
        }

        /* System function */
        if (!exec_syscall(interp, fn_name)) {
            if (interp->verbose)
                fprintf(stderr, "[interp] unknown function: %s\n", fn_name);
            /* Unknown functions are silently skipped */
        }
        break;
    }

    case OP_RET: {
        if (interp->call_stack.top == 0) {
            /* Return from entrypoint: terminate */
            interp->ip = interp->current_block ? interp->current_block->count : 0;
        } else {
            CallFrame *frame = &interp->call_stack.frames[--interp->call_stack.top];
            interp->current_block = frame->block;
            interp->ip            = frame->ret_ip;
            build_label_map(&interp->label_map, interp->current_block);
        }
        break;
    }

    case OP_LABEL:
        /* Labels are pre-indexed; just skip at runtime */
        break;

    case OP_JMP: {
        const char *label = cmd->op1.str_val;
        int offset = resolve_label(&interp->label_map, label);
        if (offset < 0) {
            fprintf(stderr, "[interp] undefined label: %s\n", label);
            return -1;
        }
        interp->ip = offset;
        break;
    }

    case OP_JE: {
        /* Jump if top of stack is truthy (after comparison) */
        Value cond;
        stack_pop(&interp->stack, &cond);
        if (val_truthy(cond)) {
            const char *label = cmd->op1.str_val;
            int offset = resolve_label(&interp->label_map, label);
            if (offset < 0) {
                fprintf(stderr, "[interp] undefined label: %s\n", label);
                return -1;
            }
            interp->ip = offset;
        }
        break;
    }

    case OP_CMP: {
        /* cmp [mem_idx], value
           Compares memory slot with a literal or second operand.
           Pushes 1 (true) or 0 (false) onto stack.
        */
        Value left = val_nil(), right = val_nil();
        int   ok_l = 0, ok_r = 0;

        if (cmd->op1.kind == OPERAND_MEMORY || cmd->op1.kind == OPERAND_GLOBAL_MEMORY) {
            /* Mirror ResolveMemory: use global when not in a call frame */
            int use_global = (cmd->op1.is_global ||
                              cmd->op1.kind == OPERAND_GLOBAL_MEMORY ||
                              interp->call_stack.top == 0);
            Memory *mem = use_global ? &interp->global_memory : &interp->memory;
            ok_l = memory_get(mem, cmd->op1.mem_index, &left);
            /* Fallback: try the other memory region */
            if (!ok_l) {
                Memory *other = use_global ? &interp->memory : &interp->global_memory;
                ok_l = memory_get(other, cmd->op1.mem_index, &left);
            }
        } else if (cmd->op1.kind == OPERAND_INT) {
            left = val_int(cmd->op1.int_val); ok_l = 1;
        } else if (cmd->op1.kind == OPERAND_STRING) {
            left = val_str(cmd->op1.str_val); ok_l = 1;
        }

        if (cmd->op2.kind == OPERAND_INT) {
            right = val_int(cmd->op2.int_val); ok_r = 1;
        } else if (cmd->op2.kind == OPERAND_FLOAT) {
            right = val_float(cmd->op2.float_val); ok_r = 1;
        } else if (cmd->op2.kind == OPERAND_STRING) {
            right = val_str(cmd->op2.str_val); ok_r = 1;
        }

        int result = (ok_l && ok_r) ? val_equal(left, right) : 0;
        stack_push(&interp->stack, val_int(result ? 1 : 0));
        break;
    }

    case OP_DEF: {
        /* def: create a new object/value from type on stack */
        Value top;
        if (!stack_pop(&interp->stack, &top)) break;
        /* Just push a nil object with the type label for now */
        stack_push(&interp->stack, top);
        break;
    }

    case OP_DEFGEN: {
        /* defgen: generic type instantiation - simplified */
        Value arity_val;
        stack_pop(&interp->stack, &arity_val);
        long n = (arity_val.kind == VAL_INT) ? arity_val.int_val : 0;
        for (long i = 0; i < n; i++)
            stack_pop(&interp->stack, NULL);
        Value obj;
        stack_pop(&interp->stack, &obj);
        stack_push(&interp->stack, obj);
        break;
    }

    case OP_CALLOBJ: {
        /* callobj method_name: call method on top object */
        Value arity_val;
        stack_pop(&interp->stack, &arity_val);
        long n = (arity_val.kind == VAL_INT) ? arity_val.int_val : 0;
        for (long i = 0; i < n; i++)
            stack_pop(&interp->stack, NULL);
        Value obj;
        stack_pop(&interp->stack, &obj);
        /* Return nil for now */
        stack_push(&interp->stack, val_nil());
        break;
    }

    case OP_AWAITOBJ:
    case OP_AWAIT: {
        /* In C runtime, values are already resolved; just leave top as-is */
        break;
    }

    case OP_STREAMWAIT:
    case OP_STREAMWAITOBJ: {
        /* Stream operations - not supported in basic runtime */
        if (interp->verbose)
            fprintf(stderr, "[interp] streamwait: not supported in basic runtime\n");
        /* Pop and push nil */
        Value v;
        stack_pop(&interp->stack, &v);
        stack_push(&interp->stack, val_nil());
        break;
    }

    case OP_GETOBJ: {
        /* stack: obj, member_name -> obj.member_name */
        Value member_name, obj;
        stack_pop(&interp->stack, &member_name);
        stack_pop(&interp->stack, &obj);
        /* Return nil for now - object system not implemented */
        stack_push(&interp->stack, val_nil());
        break;
    }

    case OP_SETOBJ: {
        /* stack: value, member_name, obj -> updated_obj */
        Value value, member_name, obj;
        stack_pop(&interp->stack, &value);
        stack_pop(&interp->stack, &member_name);
        stack_pop(&interp->stack, &obj);
        /* Return obj unchanged */
        stack_push(&interp->stack, obj);
        break;
    }

    case OP_ADDVERTEX:
    case OP_ADDRELATION:
    case OP_ADDSHAPE: {
        /* Space operations - not supported in basic C runtime */
        if (interp->verbose) {
            fprintf(stderr, "[interp] space operation not supported: opcode=%d\n",
                    cmd->opcode);
        }
        break;
    }

    default:
        if (interp->verbose)
            fprintf(stderr, "[interp] unknown opcode: %d\n", cmd->opcode);
        break;
    }

    return 1;
}

/* ------------------------------------------------------------------ */
/* Public: init and run                                                */
/* ------------------------------------------------------------------ */

void interpreter_init(Interpreter *interp)
{
    memset(interp, 0, sizeof(*interp));
    stack_init(&interp->stack);
    memory_init(&interp->memory);
    memory_init(&interp->global_memory);
}

int interpreter_run(Interpreter *interp, ExecutableUnit *unit)
{
    interp->unit          = unit;
    interp->current_block = &unit->entry_point;
    interp->ip            = 0;
    interp->call_stack.top = 0;

    build_label_map(&interp->label_map, &unit->entry_point);
    stack_init(&interp->stack);
    memory_init(&interp->memory);
    memory_init(&interp->global_memory);

    while (interp->current_block) {
        /* Implicit return when reaching end of block */
        if (interp->ip >= interp->current_block->count) {
            if (interp->call_stack.top == 0)
                break;
            CallFrame *frame = &interp->call_stack.frames[--interp->call_stack.top];
            interp->current_block = frame->block;
            interp->ip            = frame->ret_ip;
            build_label_map(&interp->label_map, interp->current_block);
            continue;
        }

        int r = exec_one(interp);
        if (r < 0) return 0; /* error */
        if (r == 0) break;   /* end */
    }

    return 1;
}
