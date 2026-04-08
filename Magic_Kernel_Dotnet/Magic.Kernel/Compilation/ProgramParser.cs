using System.Collections.Generic;
using Magic.Kernel.Compilation.Ast;

namespace Magic.Kernel.Compilation
{
    /// <summary>
    /// High-level parser for AGI program/source structure.
    /// Wraps the lower-level <see cref="Parser"/> which contains the actual parsing logic.
    /// </summary>
    public class ProgramParser
    {
        private readonly Parser parser;

        public ProgramParser()
        {
            parser = new Parser();
        }

        /// <summary>Parses full source code into <see cref="ProgramStructure"/>.</summary>
        public ProgramStructure ParseProgram(string sourceCode) => parser.ParseProgram(sourceCode);

        /// <summary>Parses source as a flat instruction set (for legacy flows without program/entrypoint).</summary>
        public List<InstructionNode> ParseAsInstructionSet(string sourceCode) => parser.ParseAsmStructure(sourceCode);
    }
}

