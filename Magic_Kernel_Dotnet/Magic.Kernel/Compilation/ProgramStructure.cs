using System.Collections.Generic;
using Magic.Kernel.Compilation.Ast;

namespace Magic.Kernel.Compilation
{
    public class ProgramStructure
    {
        public string Version { get; set; } = "0.0.1";
        public string? ProgramName { get; set; }
        public string? Module { get; set; }
        public string? System { get; set; }
        /// <summary>Output format: "agic" (binary/JSON) or "agiasm" (text assembly).</summary>
        public string? OutputFormat { get; set; }
        public List<StatementNode>? EntryPoint { get; set; }
        public List<TypeNode> Types { get; set; } = new List<TypeNode>();
        /// <summary>Top-level procedures: name → AST node (name, parameters, body).</summary>
        public Dictionary<string, ProcedureNode> Procedures { get; set; } = new Dictionary<string, ProcedureNode>();
        /// <summary>Top-level functions: name → AST node (name, parameters, body).</summary>
        public Dictionary<string, FunctionNode> Functions { get; set; } = new Dictionary<string, FunctionNode>();

        /// <summary>Top-level use directives to be resolved at compile time (linking).</summary>
        public List<UseNode> UseDirectives { get; set; } = new List<UseNode>();

        /// <summary>Indicates whether this is a structured program (with @AGI, program, procedure, function, entrypoint) or just a set of instructions.</summary>
        public bool IsProgramStructure { get; set; }
    }
}
