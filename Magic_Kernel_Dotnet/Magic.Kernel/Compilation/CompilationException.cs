using System;

namespace Magic.Kernel.Compilation
{
    /// <summary>Ошибка разбора/компиляции с позицией в исходном тексте.</summary>
    public class CompilationException : Exception
    {
        public int Position { get; }

        public CompilationException(string message, int position = -1)
            : base(message)
        {
            Position = position;
        }

        public CompilationException(string message, Exception inner)
            : base(message, inner)
        {
            Position = -1;
        }
    }
}
