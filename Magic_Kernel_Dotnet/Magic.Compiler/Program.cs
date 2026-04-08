using Magic.Kernel.Compilation;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Magic.Compiler
{
    class Program
    {
        private static readonly string[] SpinnerCharsUnicode = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        private static readonly string[] SpinnerCharsAscii = { "|", "/", "-", "\\" };
        private static int _spinnerIndex = 0;

        static async Task<int> Main(string[] args)
        {
            // Try to enable UTF-8 so spinners/box drawing render nicely on Windows.
            // If terminal doesn't support it, we fall back to ASCII spinner automatically.
            TryEnableUtf8Output();

            PrintHeader();

            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            var noAnim = args.Any(a => string.Equals(a, "--no-anim", StringComparison.OrdinalIgnoreCase));
            var demoDelayArg = args.FirstOrDefault(a => a.StartsWith("--demo-delay-ms=", StringComparison.OrdinalIgnoreCase));
            var demoDelayMs = ParseDemoDelayMs(demoDelayArg) ?? ParseDemoDelayMsFromEnv() ?? 0;
            var outputFormat = GetOutputFormatFromArgs(args);
            args = args.Where(a => !string.Equals(a, "--no-anim", StringComparison.OrdinalIgnoreCase)).ToArray();
            args = args.Where(a => !a.StartsWith("--demo-delay-ms=", StringComparison.OrdinalIgnoreCase)).ToArray();
            args = FilterOutputFormatArgs(args);

            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            var inputFile = args[0];
            
            if (!File.Exists(inputFile))
            {
                PrintError($"File '{inputFile}' not found.");
                return 1;
            }

            if (!inputFile.EndsWith(".agi", StringComparison.OrdinalIgnoreCase))
            {
                PrintWarning($"File '{inputFile}' does not have .agi extension.");
            }

            try
            {
                PrintInfo($"Reading source file: {Path.GetFileName(inputFile)}");

                var compiler = new Magic.Kernel.Compilation.Compiler();
                
                var useAnim =
                    !noAnim &&
                    !Console.IsOutputRedirected &&
                    !string.Equals(Environment.GetEnvironmentVariable("MAGIC_NO_ANIM"), "1", StringComparison.OrdinalIgnoreCase);

                CancellationTokenSource? cts = null;
                Task? spinnerTask = null;
                if (useAnim)
                {
                    PrintInfo("Compiling...");
                    cts = new CancellationTokenSource();
                    spinnerTask = ShowSpinnerAsync(cts.Token, "Compiling");
                }
                else
                {
                    PrintInfo("Compiling...");
                }
                
                var result = await compiler.CompileFileAsync(inputFile);
                
                // Visual demo: keep the spinner running for a bit so you can see it.
                // Opt-in via --demo-delay-ms=NNN or MAGIC_DEMO_DELAY_MS=NNN.
                if (useAnim && demoDelayMs > 0)
                {
                    await Task.Delay(demoDelayMs);
                }

                if (cts != null)
                {
                    cts.Cancel();
                    if (spinnerTask != null) await spinnerTask;
                }

                if (!result.Success)
                {
                    PrintError($"Compilation failed: {result.ErrorMessage}");
                    return 1;
                }

                if (result.Result == null)
                {
                    PrintError("Compilation succeeded but result is null.");
                    return 1;
                }

                // Формат вывода из args (--output agiasm|agic), по умолчанию agic.
                // По умолчанию сохраняем артефакты в корневую папку `bin/` (как ожидается в примерах).
                var format = outputFormat ?? "agic";
                result.Result!.OutputFormat = format;

                var outExt = format == "agiasm" ? ".agiasm" : ".agic";
                var outName = Path.GetFileName(Path.ChangeExtension(inputFile, outExt));

                var repoRoot = Directory.GetCurrentDirectory();

                // 1) requirement: bin/ in repo root
                var binDir = Path.Combine(repoRoot, "bin");
                Directory.CreateDirectory(binDir);
                var outputFile = Path.Combine(binDir, outName);

                // 2) runtime: SpaceEnvironment resolves paths relative to design/Space
                var spaceBinDir = Path.Combine(repoRoot, "design", "Space", "bin");
                Directory.CreateDirectory(spaceBinDir);
                var spaceOutputFile = Path.Combine(spaceBinDir, outName);
                
                PrintInfo($"Saving compiled output: {Path.GetFileName(outputFile)} ({format})");
                
                // Сохраняем скомпилированный результат (в оба bin-направления).
                await result.Result.SaveAsync(outputFile);
                if (!string.Equals(spaceOutputFile, outputFile, StringComparison.OrdinalIgnoreCase))
                    await result.Result.SaveAsync(spaceOutputFile);

                PrintSuccess("Compilation successful!");
                PrintSeparator();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  Input:  {inputFile}");
                Console.WriteLine($"  Output: {outputFile}");
                Console.ResetColor();
                PrintSeparator();
                
                return 0;
            }
            catch (Exception ex)
            {
                PrintError($"Error: {ex.Message}");
                if (ex.StackTrace != null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"\n{ex.StackTrace}");
                    Console.ResetColor();
                }
                return 1;
            }
        }

        static void PrintHeader()
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            Console.Write("║");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("  Magic: Meta AGI Compiler v.0.0.1".PadRight(57));
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();
        }

        static void PrintUsage()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Usage: Magic.Compiler [options] <file.agi>");
            Console.WriteLine("       Compiles .agi file and saves result to .agic or .agiasm");
            Console.WriteLine("Options:");
            Console.WriteLine("       --output agiasm|agic, -o agiasm|agic   Output format (default: agic)");
            Console.WriteLine("       --no-anim   Disable compile animation (also: MAGIC_NO_ANIM=1)");
            Console.WriteLine("       --demo-delay-ms=NNN   Artificial delay (ms) to visually check animation (also: MAGIC_DEMO_DELAY_MS)");
            Console.ResetColor();
        }

        static string? GetOutputFormatFromArgs(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        var val = args[i + 1].Trim().ToLowerInvariant();
                        if (val is "agiasm" or "agic") return val;
                    }
                    return null;
                }
                if (a.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
                {
                    var val = a.Substring(9).Trim().ToLowerInvariant();
                    if (val is "agiasm" or "agic") return val;
                    return null;
                }
            }
            return null;
        }

        static string[] FilterOutputFormatArgs(string[] args)
        {
            var list = new List<string>();
            var skipNext = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (skipNext) { skipNext = false; continue; }
                var a = args[i];
                if (string.Equals(a, "--output", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-o", StringComparison.OrdinalIgnoreCase))
                {
                    skipNext = true;
                    continue;
                }
                if (a.StartsWith("--output=", StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(a);
            }
            return list.ToArray();
        }

        static void PrintInfo(string message)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write("ℹ ");
            Console.ResetColor();
            Console.WriteLine(message);
        }

        static void PrintSuccess(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("✓ ");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(message);
            Console.ResetColor();
        }

        static void PrintWarning(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("⚠ ");
            Console.ResetColor();
            Console.WriteLine(message);
        }

        static void PrintError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("✗ ");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(message);
            Console.ResetColor();
        }

        static void PrintSeparator()
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("─────────────────────────────────────────────────────────────");
            Console.ResetColor();
        }

        static async Task ShowSpinnerAsync(CancellationToken cancellationToken, string label)
        {
            var spinnerChars = UseUnicodeSpinner() ? SpinnerCharsUnicode : SpinnerCharsAscii;

            var originalLeft = Console.CursorLeft;
            var originalTop = Console.CursorTop;
            var originalCursorVisible = Console.CursorVisible;
            var start = DateTime.UtcNow;

            try
            {
                Console.CursorVisible = false;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var spinner = spinnerChars[_spinnerIndex % spinnerChars.Length];
                    var elapsed = DateTime.UtcNow - start;
                    var text = $"{spinner} {label} ({elapsed.TotalSeconds:0.0}s)";

                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.SetCursorPosition(0, originalTop);
                    Console.Write(text.PadRight(Math.Max(Console.WindowWidth - 1, text.Length)));
                    Console.ResetColor();
                    _spinnerIndex++;
                    await Task.Delay(100, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            finally
            {
                // Clear spinner line & restore cursor state
                try
                {
                    Console.SetCursorPosition(0, originalTop);
                    Console.Write(new string(' ', Math.Max(Console.WindowWidth - 1, 1)));
                    Console.SetCursorPosition(originalLeft, originalTop);
                }
                catch
                {
                    // ignore console errors
                }
                Console.CursorVisible = originalCursorVisible;
            }
        }

        private static bool UseUnicodeSpinner()
        {
            // Heuristic: if output encoding is UTF-8 and we're not redirected, try unicode frames.
            if (Console.IsOutputRedirected) return false;
            return Console.OutputEncoding.WebName.Equals("utf-8", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryEnableUtf8Output()
        {
            try
            {
                if (!Console.OutputEncoding.WebName.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                {
                    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                }
            }
            catch
            {
                // ignore (some hosts disallow changing encoding)
            }
        }

        private static int? ParseDemoDelayMs(string? arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return null;
            var parts = arg.Split('=', 2);
            if (parts.Length != 2) return null;
            if (int.TryParse(parts[1], out var ms) && ms >= 0) return ms;
            return null;
        }

        private static int? ParseDemoDelayMsFromEnv()
        {
            var raw = Environment.GetEnvironmentVariable("MAGIC_DEMO_DELAY_MS");
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (int.TryParse(raw, out var ms) && ms >= 0) return ms;
            return null;
        }
    }
}
