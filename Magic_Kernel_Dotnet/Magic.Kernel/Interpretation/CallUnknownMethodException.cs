using System;

namespace Magic.Kernel.Interpretation
{
    /// <summary>Method name is not supported in CallObj/CallAsync.</summary>
    public class CallUnknownMethodException : Exception
    {
        public string MethodName { get; }
        public object? Target { get; }

        public CallUnknownMethodException(string methodName, object? target = null)
            : base($"CallObj: unknown method '{methodName ?? ""}' on {target?.GetType().Name ?? "null"}.")
        {
            MethodName = methodName ?? "";
            Target = target;
        }

        public CallUnknownMethodException(string message, string? methodName = null, object? target = null)
            : base(message)
        {
            MethodName = methodName ?? "";
            Target = target;
        }
    }
}
