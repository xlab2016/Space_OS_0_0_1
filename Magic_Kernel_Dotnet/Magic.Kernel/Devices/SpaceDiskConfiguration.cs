using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public class SpaceDiskConfiguration
    {
        /// <summary>Namespace for all keys: {spaceName}:vertices:index:..., etc. When set from program: module_name + "|" + program_name.</summary>
        public string? SpaceName { get; set; }

        public long? VertexSequenceIndex { get; set; }
        public long? RelationSequenceIndex { get; set; }
        public long? ShapeSequenceIndex { get; set; }

        public IProjectorDevice Projector { get; set; }
    }
}
