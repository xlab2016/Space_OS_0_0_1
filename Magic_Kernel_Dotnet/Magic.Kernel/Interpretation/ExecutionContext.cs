using System.Threading;
using Magic.Kernel.Compilation;

namespace Magic.Kernel.Interpretation
{
    /// <summary>
    /// Ambient execution context for the interpreter, exposing the currently running ExecutableUnit
    /// via AsyncLocal so that lower-level components (drivers, devices) can access InstanceIndex safely.
    /// </summary>
    public static class ExecutionContext
    {
        private static readonly AsyncLocal<ExecutableUnit?> currentUnit = new AsyncLocal<ExecutableUnit?>();

        public static ExecutableUnit? CurrentUnit
        {
            get => currentUnit.Value;
            set => currentUnit.Value = value;
        }

        /// <summary>
        /// Builds unified console prefix "executionUnit.Name: executionUnit.InstanceIndex: " (skipping missing parts).
        /// </summary>
        public static string GetPrefix(ExecutableUnit? unit = null)
        {
            unit ??= CurrentUnit;
            if (unit == null)
                return string.Empty;

            var namePart = string.IsNullOrWhiteSpace(unit.Name) ? string.Empty : unit.Name + ": ";
            var indexPart = unit.InstanceIndex is int i ? i + ": " : string.Empty;
            return namePart + indexPart;
        }
    }
}

