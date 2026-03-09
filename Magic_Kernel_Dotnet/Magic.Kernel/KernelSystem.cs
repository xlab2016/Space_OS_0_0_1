using Magic.Kernel.Space;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel
{
    public static class KernelSystem
    {
        public static string ToString(EntityType entityType)
        {
            switch (entityType)
            {
                case EntityType.Vertex:
                    return "vertex";
                case EntityType.Relation:
                    return "relation";
                case EntityType.Shape:
                    return "shape";
                default:
                    return "unknown";
            }
        }
    }
}
