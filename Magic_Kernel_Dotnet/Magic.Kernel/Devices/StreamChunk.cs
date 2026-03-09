using System;

namespace Magic.Kernel.Devices
{
    public class StreamChunk : IStreamChunk
    {
        public long ChunkSize { get; set; }

        public StructurePosition Position { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public DataFormat DataFormat { get; set; }
        public ApplicationFormat ApplicationFormat { get; set; }
    }
}
