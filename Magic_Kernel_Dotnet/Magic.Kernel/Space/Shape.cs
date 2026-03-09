using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Space
{
    public class Shape : EntityBase
    {
        public Position? Origin { get; set; }

        public List<Vertex>? Vertices { get; set; }
        public List<long>? VertexIndices { get; set; }

        public Shape()
        {
            Type = EntityType.Shape;
        }
    }
}
