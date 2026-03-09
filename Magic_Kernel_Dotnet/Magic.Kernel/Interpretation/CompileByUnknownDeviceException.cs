using System;

namespace Magic.Kernel.Interpretation
{
    /// <summary>Compile was called with an object that is not a stream device (IStreamDevice or StreamHandle).</summary>
    public class CompileByUnknownDeviceException : Exception
    {
        public object? DataObj { get; }

        public CompileByUnknownDeviceException(object? dataObj)
            : base($"Compile: expected IStreamDevice or StreamHandle, got {dataObj?.GetType().Name ?? "null"}.")
        {
            DataObj = dataObj;
        }
    }
}
