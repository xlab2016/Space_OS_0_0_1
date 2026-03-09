using System;

namespace Magic.Kernel.Interpretation
{
    /// <summary>Unknown type name in Def instruction.</summary>
    public class DefTypeUnknownException : Exception
    {
        public object? TypeObj { get; }

        public DefTypeUnknownException(object? typeObj)
            : base($"Unknown Def type: {typeObj ?? "null"}.")
        {
            TypeObj = typeObj;
        }

        public DefTypeUnknownException(string message, object? typeObj = null)
            : base(message)
        {
            TypeObj = typeObj;
        }
    }
}
