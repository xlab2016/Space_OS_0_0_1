using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Space
{
    public class EntityBase
    {
        public EntityType Type { get; set; } = EntityType.Undefined;
        public long? Index { get; set; }
        public string? Name { get; set; }
        public float? Weight { get; set; }
        public float? Energy { get; set; }

        public Position? Position { get; set; }
        public EntityData? Data { get; set; }
    }
}
