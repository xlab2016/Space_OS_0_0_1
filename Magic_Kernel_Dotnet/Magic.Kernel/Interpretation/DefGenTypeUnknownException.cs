using System;

namespace Magic.Kernel.Interpretation
{
    /// <summary>Unknown generalization type in DefGen instruction.</summary>
    public class DefGenTypeUnknownException : Exception
    {
        public object? GenValue { get; }

        public DefGenTypeUnknownException(object? genValue)
            : base($"Unknown DefGen generalization type: {genValue ?? "null"}.")
        {
            GenValue = genValue;
        }

        public DefGenTypeUnknownException(string message, object? genValue = null)
            : base(message)
        {
            GenValue = genValue;
        }
    }
}
