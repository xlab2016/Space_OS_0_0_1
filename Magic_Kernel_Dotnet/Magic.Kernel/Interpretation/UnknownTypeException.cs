using System;

namespace Magic.Kernel.Interpretation
{
    /// <summary>Object does not implement IDefType in CallObj instruction.</summary>
    public class UnknownTypeException : Exception
    {
        public object? Obj { get; }

        public UnknownTypeException(object? obj)
            : base($"CallObj: object is not IDefType: {obj?.GetType().Name ?? "null"}.")
        {
            Obj = obj;
        }

        public UnknownTypeException(string message, object? obj = null)
            : base(message)
        {
            Obj = obj;
        }
    }
}
