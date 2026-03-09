using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Compilation
{
    public class Compiler
    {
        public async Task<List<CompilationResult>> CompileDirectoryAsync(string directoryPath)
        {
            var result = new List<CompilationResult>();
            var sourceFiles = Directory.GetFiles(directoryPath, "*.agi", SearchOption.AllDirectories);
            foreach (var file in sourceFiles)
            {
                var fileResult = await CompileFileAsync(file);
                result.Add(fileResult);
            }
            return result;
        }

        public async Task<CompilationResult> CompileFileAsync(string filePath)
        {
            string sourceCode = await File.ReadAllTextAsync(filePath);
            return await CompileAsync(sourceCode);
        }

        public async Task<CompilationResult> CompileAsync(string sourceCode)
        {
            try
            {
                var parser = new Parser();
                var semanticAnalyzer = new SemanticAnalyzer();
                var executableUnit = new ExecutableUnit();

                var programStructure = parser.ParseProgram(sourceCode);

                executableUnit.Version = programStructure.Version;
                executableUnit.Name = programStructure.ProgramName;
                executableUnit.Module = programStructure.Module;
                executableUnit.System = programStructure.System;

                var analyzed = semanticAnalyzer.AnalyzeProgram(programStructure, parser, sourceCode);
                executableUnit.EntryPoint = analyzed.EntryPoint;
                executableUnit.Procedures = analyzed.Procedures;
                executableUnit.Functions = analyzed.Functions;

                return new CompilationResult
                {
                    Success = true,
                    Result = executableUnit
                };
            }
            catch (CompilationException cex)
            {
                // Добавляем позицию (строка/колонка), если она есть.
                var (line, column) = GetLineAndColumn(sourceCode, cex.Position);
                var posSuffix = cex.Position >= 0
                    ? $" (line {line}, column {column})"
                    : string.Empty;

                return new CompilationResult
                {
                    Success = false,
                    ErrorMessage = cex.Message + posSuffix
                };
            }
            catch (Exception ex)
            {
                return new CompilationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private static (int Line, int Column) GetLineAndColumn(string source, int position)
        {
            if (position < 0 || position > source.Length)
                return (0, 0);

            var line = 1;
            var col = 1;
            for (var i = 0; i < position; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                    col = 1;
                }
                else
                {
                    col++;
                }
            }
            return (line, col);
        }
    }
}
