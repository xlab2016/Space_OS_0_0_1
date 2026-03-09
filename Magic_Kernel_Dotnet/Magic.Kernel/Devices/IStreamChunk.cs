using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public interface IStreamChunk
    {
        DataFormat DataFormat { get; set; }
        ApplicationFormat ApplicationFormat { get; set; }
        long ChunkSize { get; set; }
        StructurePosition Position { get; set; }
        byte[] Data { get; set; }
    }
}
