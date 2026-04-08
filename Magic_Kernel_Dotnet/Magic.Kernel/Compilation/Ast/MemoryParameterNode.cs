namespace Magic.Kernel.Compilation.Ast
{
    public class MemoryParameterNode : ParameterNode
    {
        /// <summary>Physical storage index (for runtime).</summary>
        public long Index { get; set; }

        /// <summary>Logical index (per-function/procedure), used for AGIASM/debug formatting.</summary>
        public long? LogicalIndex { get; set; }
    }
}
