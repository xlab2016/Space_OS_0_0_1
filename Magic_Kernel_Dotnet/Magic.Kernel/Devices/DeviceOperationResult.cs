namespace Magic.Kernel.Devices
{
    /// <summary>State of a device operation (replaces previous enum-only result).</summary>
    public enum DeviceOperationState
    {
        Success = 0,
        Failed = 1,
        NotFound = 2,
        InvalidState = 3,
        IOError = 4,
        Unauthorized = 5,
        Timeout = 6,
        Cancelled = 7,
        NotSupported = 8,
        InsufficientMemory = 9,
    }

    /// <summary>Result of a device operation with optional error details.</summary>
    public sealed class DeviceOperationResult
    {
        public DeviceOperationState State { get; }
        public string? ErrorMessage { get; }
        public int? ErrorCode { get; }

        public bool IsSuccess => State == DeviceOperationState.Success;

        private DeviceOperationResult(DeviceOperationState state, string? errorMessage = null, int? errorCode = null)
        {
            State = state;
            ErrorMessage = errorMessage;
            ErrorCode = errorCode;
        }

        public static readonly DeviceOperationResult Success = new(DeviceOperationState.Success);

        public static DeviceOperationResult Fail(DeviceOperationState state = DeviceOperationState.Failed, string? errorMessage = null, int? errorCode = null)
            => new(state, errorMessage, errorCode);

        public static DeviceOperationResult NotFound(string? message = null, int? code = null)
            => new(DeviceOperationState.NotFound, message, code);

        public static DeviceOperationResult IOError(string? message = null, int? code = null)
            => new(DeviceOperationState.IOError, message, code);

        public static DeviceOperationResult NotSupported(string? message = null)
            => new(DeviceOperationState.NotSupported, message, null);

        public static DeviceOperationResult InsufficientMemory(string? message = null, int? code = null)
            => new(DeviceOperationState.InsufficientMemory, message, code);

        public override bool Equals(object? obj)
            => obj is DeviceOperationResult other && State == other.State && ErrorCode == other.ErrorCode && ErrorMessage == other.ErrorMessage;

        public override int GetHashCode()
            => HashCode.Combine(State, ErrorCode, ErrorMessage);

        public static bool operator ==(DeviceOperationResult? a, DeviceOperationResult? b)
        {
            if (a is null) return b is null;
            return b is not null && a.Equals(b);
        }

        public static bool operator !=(DeviceOperationResult? a, DeviceOperationResult? b) => !(a == b);
    }
}
