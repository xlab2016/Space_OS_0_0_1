using System.Collections.Generic;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel;

namespace Magic.Kernel.Compilation
{
    /// <summary>Результат анализа программы: скомпилированные entrypoint, процедуры и функции.</summary>
    public class AnalyzedProgram
    {
        public ExecutionBlock EntryPoint { get; set; } = new ExecutionBlock();
        public Dictionary<string, Processor.Procedure> Procedures { get; set; } = new Dictionary<string, Processor.Procedure>();
        public Dictionary<string, Processor.Function> Functions { get; set; } = new Dictionary<string, Processor.Function>();
    }

    public class SemanticAnalyzer
    {
        private readonly Assembler _assembler;
        private readonly StatementLoweringCompiler _statementLoweringCompiler;

        public SemanticAnalyzer()
        {
            _assembler = new Assembler();
            _statementLoweringCompiler = new StatementLoweringCompiler();
        }

        public Command Analyze(InstructionNode instruction)
        {
            var opcode = MapOpcode(instruction.Opcode);
            return _assembler.Emit(opcode, instruction.Parameters);
        }

        /// <summary>Analyze instruction and add resulting command(s) to block. For zero-arg Call, prepends Push(arity=0) unless arity was already pushed manually right before Call.</summary>
        private void AddAnalyzedCommand(ExecutionBlock block, InstructionNode instructionNode)
        {
            var cmd = Analyze(instructionNode);
            if (ShouldInjectZeroArity(block, instructionNode, cmd))
                block.Add(_assembler.EmitPushIntLiteral(0));
            block.Add(cmd);
        }

        private static bool ShouldInjectZeroArity(ExecutionBlock block, InstructionNode instructionNode, Command cmd)
        {
            if (cmd.Opcode != Opcodes.Call)
                return false;

            var parameters = instructionNode.Parameters;
            if (parameters == null || parameters.Count == 0)
                return true;

            // Call syntax always includes FunctionNameParameterNode.
            // If there are extra params, arity is supplied by caller/lowering.
            var hasOnlyFunctionName = parameters.TrueForAll(p => p is FunctionNameParameterNode);
            if (!hasOnlyFunctionName)
                return false;

            // Lowered patterns like ":time -> get" already emit explicit arity push.
            if (block.Count > 0 &&
                block[^1].Opcode == Opcodes.Push &&
                block[^1].Operand1 is PushOperand pushOperand &&
                pushOperand.Kind == "IntLiteral")
            {
                return false;
            }

            return true;
        }

        /// <summary>Компилирует полную структуру программы (AST) в команды. Если entrypoint пуст — парсит sourceCode как плоский набор инструкций (fallback).
        /// Проверка консистентности переменных: при использовании необъявленной переменной выбрасывается <see cref="UndeclaredVariableException"/>.</summary>
        public AnalyzedProgram AnalyzeProgram(ProgramStructure programStructure, Parser parser, string sourceCode)
        {
            var result = new AnalyzedProgram();
            if (programStructure.Prelude.Count > 0)
            {
                foreach (var instructionNode in LowerToInstructions(programStructure.Prelude, registerGlobals: true))
                    AddAnalyzedCommand(result.EntryPoint, instructionNode);
            }
            if (programStructure.EntryPoint != null && programStructure.EntryPoint.Count > 0)
            {
                foreach (var instructionNode in LowerToInstructions(programStructure.EntryPoint))
                    AddAnalyzedCommand(result.EntryPoint, instructionNode);
            }
            else
            {
                result.EntryPoint = AnalyzeInstructions(parser.ParseAsInstructionSet(sourceCode));
            }
            foreach (var proc in programStructure.Procedures)
            {
                var procedure = new Processor.Procedure { Name = proc.Key };
                foreach (var instructionNode in LowerToInstructions(proc.Value))
                    AddAnalyzedCommand(procedure.Body, instructionNode);
                // Явный ret в конце процедуры (для корректного asm-дампа и локальных подпрограмм).
                if (procedure.Body.Count == 0 || procedure.Body[procedure.Body.Count - 1].Opcode != Opcodes.Ret)
                    procedure.Body.Add(_assembler.Emit(Opcodes.Ret, null));
                result.Procedures[proc.Key] = procedure;
            }
            foreach (var func in programStructure.Functions)
            {
                var function = new Processor.Function { Name = func.Key };
                foreach (var instructionNode in LowerToInstructions(func.Value))
                    AddAnalyzedCommand(function.Body, instructionNode);
                // Явный ret в конце функции.
                if (function.Body.Count == 0 || function.Body[function.Body.Count - 1].Opcode != Opcodes.Ret)
                    function.Body.Add(_assembler.Emit(Opcodes.Ret, null));
                result.Functions[func.Key] = function;
            }
            return result;
        }

        /// <summary>Компилирует список AST-инструкций в блок команд (для fallback без структуры программы).</summary>
        public ExecutionBlock AnalyzeInstructions(IEnumerable<InstructionNode> nodes)
        {
            var block = new ExecutionBlock();
            foreach (var node in nodes)
                AddAnalyzedCommand(block, node);
            return block;
        }

        private IEnumerable<InstructionNode> LowerToInstructions(IEnumerable<AstNode> bodyNodes, bool registerGlobals = false)
        {
            var instructions = new List<InstructionNode>();
            var statementLines = new List<string>();

            foreach (var node in bodyNodes)
            {
                if (node is StatementLineNode statementLineNode)
                {
                    statementLines.Add(statementLineNode.Text);
                    continue;
                }

                FlushStatementLines(statementLines, instructions, registerGlobals);

                if (node is InstructionNode instructionNode)
                {
                    instructions.Add(instructionNode);
                }
            }

            FlushStatementLines(statementLines, instructions, registerGlobals);
            return instructions;
        }

        private void FlushStatementLines(List<string> statementLines, List<InstructionNode> instructions, bool registerGlobals = false)
        {
            if (statementLines.Count == 0)
                return;

            instructions.AddRange(_statementLoweringCompiler.Lower(statementLines, registerGlobals));
            statementLines.Clear();
        }

        private Opcodes MapOpcode(string opcode)
        {
            return opcode.ToLower() switch
            {
                "addvertex" => Opcodes.AddVertex,
                "addrelation" => Opcodes.AddRelation,
                "addshape" => Opcodes.AddShape,
                "call" => Opcodes.Call,
                "push" => Opcodes.Push,
                "pop" => Opcodes.Pop,
                "syscall" => Opcodes.SysCall,
                "ret" => Opcodes.Ret,
                "move" => Opcodes.Move,
                "getvertex" => Opcodes.GetVertex,
                "def" => Opcodes.Def,
                "defgen" => Opcodes.DefGen,
                "callobj" => Opcodes.CallObj,
                "awaitobj" => Opcodes.AwaitObj,
                "streamwaitobj" => Opcodes.StreamWaitObj,
                "await" => Opcodes.Await,
                "label" => Opcodes.Label,
                "cmp" => Opcodes.Cmp,
                "je" => Opcodes.Je,
                "jmp" => Opcodes.Jmp,
                "getobj" => Opcodes.GetObj,
                "setobj" => Opcodes.SetObj,
                "streamwait" => Opcodes.StreamWait,
                _ => Opcodes.Nop
            };
        }
    }
}
