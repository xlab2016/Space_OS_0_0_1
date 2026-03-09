using System.IO;

namespace Magic.Kernel.Devices
{
    /// <summary>Application/language format when <see cref="DataFormat"/> is <see cref="DataFormat.Code"/>.</summary>
    public enum ApplicationFormat
    {
        Unknown,
        Python,
        CSharp,
        FSharp,
        VB,
        JavaScript,
        TypeScript,
        Java,
        Kotlin,
        Go,
        Rust,
        C,
        Cpp,
        Ruby,
        Php,
        Swift,
        Sql,
        Shell,
        PowerShell,
        Lua,
        R,
        Scala,
        Haskell,
        Dart,
        Yaml,
        Markdown,
    }

    /// <summary>Detection of <see cref="ApplicationFormat"/> from path/extension (and optionally content).</summary>
    public static class ApplicationFormatDetection
    {
        /// <summary>Detect application format from file path (extension).</summary>
        public static ApplicationFormat FromPath(string path)
        {
            var ext = (Path.GetExtension(path) ?? "").TrimStart('.').ToLowerInvariant();
            return FromExtension(ext);
        }

        /// <summary>Detect application format from extension only (e.g. "cs", "py").</summary>
        public static ApplicationFormat FromExtension(string extension)
        {
            var ext = (extension ?? "").TrimStart('.').ToLowerInvariant();
            return ext switch
            {
                "py" or "pyw" or "pyi" => ApplicationFormat.Python,
                "cs" or "csx" => ApplicationFormat.CSharp,
                "fs" or "fsx" or "fsi" => ApplicationFormat.FSharp,
                "vb" or "vbs" => ApplicationFormat.VB,
                "js" or "mjs" or "cjs" => ApplicationFormat.JavaScript,
                "ts" or "mts" or "cts" => ApplicationFormat.TypeScript,
                "java" => ApplicationFormat.Java,
                "kt" or "kts" => ApplicationFormat.Kotlin,
                "go" => ApplicationFormat.Go,
                "rs" => ApplicationFormat.Rust,
                "c" or "h" => ApplicationFormat.C,
                "cpp" or "cc" or "cxx" or "hpp" or "hxx" or "h++" => ApplicationFormat.Cpp,
                "rb" or "erb" => ApplicationFormat.Ruby,
                "php" or "phtml" => ApplicationFormat.Php,
                "swift" => ApplicationFormat.Swift,
                "sql" => ApplicationFormat.Sql,
                "sh" or "bash" or "zsh" or "ksh" => ApplicationFormat.Shell,
                "ps1" or "psm1" or "psd1" => ApplicationFormat.PowerShell,
                "lua" => ApplicationFormat.Lua,
                "r" or "rdata" or "rds" => ApplicationFormat.R,
                "scala" or "sc" => ApplicationFormat.Scala,
                "hs" or "lhs" => ApplicationFormat.Haskell,
                "dart" => ApplicationFormat.Dart,
                "yaml" or "yml" => ApplicationFormat.Yaml,
                "md" or "markdown" => ApplicationFormat.Markdown,
                _ => ApplicationFormat.Unknown
            };
        }
    }
}
