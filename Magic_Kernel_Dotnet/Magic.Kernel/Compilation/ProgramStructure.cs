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
        public List<AstNode>? EntryPoint { get; set; }
        public List<AstNode> Prelude { get; set; } = new List<AstNode>();
        public Dictionary<string, List<AstNode>> Procedures { get; set; } = new Dictionary<string, List<AstNode>>();
        public Dictionary<string, List<AstNode>> Functions { get; set; } = new Dictionary<string, List<AstNode>>();
        /// <summary>Indicates whether this is a structured program (with @AGI, program, procedure, function, entrypoint) or just a set of instructions.</summary>
        public bool IsProgramStructure { get; set; }
    }
}
