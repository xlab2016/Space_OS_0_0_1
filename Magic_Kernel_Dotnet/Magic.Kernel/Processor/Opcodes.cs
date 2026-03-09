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
        StreamWait = 23
    }
}
