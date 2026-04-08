using Magic.Kernel.Compilation;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AgiCompileTest
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            var filePath = args.Length > 0 ? args[0] : Path.Combine(repoRoot, "examples", "magic", "modularity", "use_module1.agi");

            Console.WriteLine($"Testing compilation of: {filePath}");
            Console.WriteLine();

            var compiler = new Compiler();
            var result = await compiler.CompileFileAsync(filePath);

            if (result.Success)
            {
                Console.WriteLine("SUCCESS: Compilation successful!");
                Console.WriteLine($"  Program: {result.Result?.Name}");
                Console.WriteLine($"  Procedures: {result.Result?.Procedures.Count}");
                Console.WriteLine($"  Functions: {result.Result?.Functions.Count}");
                Console.WriteLine($"  Types: {result.Result?.Types.Count}");
                return 0;
            }
            else
            {
                Console.WriteLine($"ERROR: Compilation failed!");
                Console.WriteLine($"  Message: {result.ErrorMessage}");
                return 1;
            }
        }
    }
}
