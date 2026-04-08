using System.Collections.Generic;
using System.Linq;
using Magic.Kernel.Compilation;

namespace Magic.Kernel.Interpretation;

public partial class Interpreter
{
    /// <summary>Текущий execution unit (последний переданный в InterpreteAsync / InterpreteFromEntryAsync).</summary>
    public ExecutableUnit? CurrentUnit => _unit;

    /// <summary>Стек вычислений, call stack и слои памяти на текущий момент.</summary>
    public InterpreterDebugSnapshot CaptureDebugSnapshot()
    {
        var snap = new InterpreterDebugSnapshot
        {
            UnitDisplay = InterpreterDebugFormatting.UnitLabel(_unit),
            InstructionPointer = instructionPointer
        };

        for (var i = 0; i < Stack.Count; i++)
        {
            var v = Stack[i];
            snap.EvaluationStack.Add(new InterpreterDebugStackSlot
            {
                Index = i,
                TypeName = InterpreterDebugFormatting.FormatTypeLabel(v),
                Value = InterpreterDebugFormatting.FormatValueSummary(v),
                InspectRoot = InterpreterDebugFormatting.TryBuildInspectRoot(v)
            });
        }

        var depth = 0;
        foreach (var frame in _callStack)
        {
            snap.CallStack.Add(new InterpreterDebugCallFrame
            {
                Depth = depth++,
                Name = frame.Name,
                ReturnIp = frame.ReturnIp
            });
        }

        snap.MemoryCallFrameDepth = MemoryContext.CallFrameDepth;

        void AddLayer(string name, IReadOnlyDictionary<long, object> dict)
        {
            var layer = new InterpreterDebugMemoryLayer { Name = name };
            foreach (var kv in dict.OrderBy(k => k.Key))
            {
                var val = kv.Value;
                layer.Cells.Add(new InterpreterDebugMemoryCell
                {
                    Slot = kv.Key,
                    TypeName = InterpreterDebugFormatting.FormatTypeLabel(val),
                    Value = InterpreterDebugFormatting.FormatValueSummary(val),
                    InspectRoot = InterpreterDebugFormatting.TryBuildInspectRoot(val)
                });
            }

            snap.MemoryLayers.Add(layer);
        }

        AddLayer("Global", MemoryContext.Global);
        AddLayer("Inherited", MemoryContext.Inherited);
        if (MemoryContext.CallFrameDepth == 0)
        {
            AddLayer("Local", MemoryContext.Local);
        }
        else
        {
            var fi = 0;
            foreach (var block in MemoryContext.WalkFramesBottomToTop())
            {
                AddLayer($"Local@frame{fi}", block.Local);
                if (block.Inherited.Count > 0)
                    AddLayer($"Inherited@frame{fi}", block.Inherited);
                fi++;
            }
        }

        return snap;
    }
}
