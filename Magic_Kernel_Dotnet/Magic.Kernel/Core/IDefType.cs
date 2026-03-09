using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Magic.Kernel.Devices;

namespace Magic.Kernel.Core
{
    public interface IDefType
    {
        public long? Index { get; set; }
        public string Name { get; set; }
        public StructurePosition? Position { get; set; }

        public List<IDefType> Generalizations { get; set; }

        /// <summary>Invoke object method by name with arguments (for CallObj opcode).</summary>
        Task<object?> CallObjAsync(string methodName, object?[] args);

        /// <summary>Await object-level operation (legacy AwaitObj opcode behavior).</summary>
        Task<object?> AwaitObjAsync();

        /// <summary>Generic await operation. Can await inner Task or return object itself.</summary>
        Task<object?> Await();

        /// <summary>Read next stream delta/aggregate for streamwait loops.</summary>
        Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType);
    }
}
