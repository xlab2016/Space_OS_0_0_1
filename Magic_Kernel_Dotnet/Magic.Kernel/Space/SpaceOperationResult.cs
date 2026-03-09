using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Space
{
    public enum SpaceOperationResult : int
    {
        Success = 0,
        Failed = 1,
        Exists = 2,
        InnerDeviceFailure = 3,
        NameLengthExceeded256 = 4,
        InvalidArguments = 5
    }
}
