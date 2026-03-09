using System;

namespace Magic.Kernel.Compilation
{
    /// <summary>Переменная использована, но не была объявлена.</summary>
    public class UndeclaredVariableException : CompilationException
    {
        public string VariableName { get; }

        public UndeclaredVariableException(string variableName, int position = -1)
            : base($"Variable '{variableName}' is not declared.", position)
        {
            VariableName = variableName ?? "";
        }
    }
}
