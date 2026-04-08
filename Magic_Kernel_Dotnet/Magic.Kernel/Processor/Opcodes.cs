using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Processor
{
    public enum Opcodes : int
    {
        Nop = 0,
        AddVertex = 1,
        AddRelation = 2,
        AddShape = 3,
        Call = 4,
        Push = 5,
        Pop = 6,
        SysCall = 7,
        Ret = 8,
        Move = 9,
        GetVertex = 10,
        Def = 11,
        DefGen = 12,
        CallObj = 13,
        AwaitObj = 14,
        StreamWaitObj = 15,
        Await = 16,
        Label = 17,
        Cmp = 18,
        Je = 19,
        Jmp = 20,
        GetObj = 21,
        SetObj = 22,
        StreamWait = 23,
        ACall = 24,
        /// <summary>Start lambda: collect instructions until DefExpr into body, push LambdaValue(body), advance IP past DefExpr.</summary>
        Expr = 25,
        /// <summary>End lambda body (excluded from body). When inside lambda invocation: return from lambda.</summary>
        DefExpr = 26,
        /// <summary>No-op at runtime; marks end of lambda body before DefExpr (lambda ref already on stack from Expr).</summary>
        Lambda = 27,
        /// <summary>Pop b, pop a, push (a equals b).</summary>
        Equals = 28,
        /// <summary>Pop value, push logical negation using runtime truthiness rules.</summary>
        Not = 29,
        /// <summary>Pop b, pop a, push (a &lt; b) as 1L or 0L. Uses numeric comparison when both convert to number.</summary>
        Lt = 30
        ,
        /// <summary>Pop b, pop a, push (a + b). Supports numeric addition (long/decimal) and string concatenation.</summary>
        Add = 31,
        /// <summary>Pop b, pop a, push (a - b). Numeric only (long/decimal).</summary>
        Sub = 32,
        /// <summary>Pop b, pop a, push (a * b). Numeric only (long/decimal).</summary>
        Mul = 33,
        /// <summary>Pop b, pop a, push (a / b). Numeric only (long/decimal).</summary>
        Div = 34,
        /// <summary>Pop b, pop a, push (a raised to b). Numeric via double conversion (AGI ^ operator).</summary>
        Pow = 35,
        /// <summary>Object construction opcode (semantic alias of Def, emitted for `new Type{...}` flows).</summary>
        DefObj = 36
    }
}
