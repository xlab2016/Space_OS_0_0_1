using System.Collections.Generic;

namespace Magic.Kernel.Compilation
{
    public sealed class UseNode
    {
        /// <summary>Raw module path token string from AGI source, e.g. "modularity\module1" or "lib1".</summary>
        public string ModulePath { get; set; } = string.Empty;

        /// <summary>Optional alias after "as", e.g. "internal".</summary>
        public string? Alias { get; set; }

        /// <summary>
        /// Optional explicit import signatures after "as { ... }". When null/empty, linker treats it as "import all".
        /// </summary>
        public List<UseImportSignature>? Signatures { get; set; }
    }

    public sealed class UseImportSignature
    {
        /// <summary>"function" or "procedure".</summary>
        public string Kind { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        /// <summary>Optional parameter names list (for arity validation in future).</summary>
        public List<string> ParameterNames { get; set; } = new List<string>();
    }
}

