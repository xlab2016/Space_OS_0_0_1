using Magic.Kernel.Compilation;
using Magic.Kernel.Devices.Store;
using Magic.Kernel.Devices.Streams;
using Magic.Kernel.Interpretation;

namespace Magic.Kernel.Core
{
    public sealed class ExecutionCallContext
    {
        public ExecutableUnit? Unit { get; init; }
        public Interpreter? Interpreter { get; init; }
        public DatabaseDevice? CurrentDatabase { get; init; }
        public bool IsExecutingQueryExpr { get; init; }
        public ClawSocketContext? CurrentSocket { get; init; }

        public static string GetPrefix(ExecutableUnit? unit = null)
        {
            if (unit == null)
                return string.Empty;

            var namePart = string.IsNullOrWhiteSpace(unit.Name) ? string.Empty : unit.Name + ": ";
            var indexPart = unit.InstanceIndex is int i ? i + ": " : string.Empty;
            return namePart + indexPart;
        }

        public string GetPrefix()
            => GetPrefix(Unit);
    }
}
