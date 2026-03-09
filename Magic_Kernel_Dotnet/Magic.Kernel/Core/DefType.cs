using Magic.Kernel.Devices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Core
{
    public class DefType : IDefType
    {
        public long? Index { get; set; }
        public string Name { get; set; } = "";
        public StructurePosition? Position { get; set; }
        public List<IDefType> Generalizations { get; set; } = new List<IDefType>();

        public virtual async Task<object?> Await()
        {
            throw new NotImplementedException();
        }

        public virtual async Task<object?> AwaitObjAsync()
        {
            throw new NotImplementedException();
        }

        public virtual async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            throw new NotImplementedException();
        }

        public virtual async Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
        {
            throw new NotImplementedException();
        }
    }
}
