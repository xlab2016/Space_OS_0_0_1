using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Space
{
    public class Relation : EntityBase
    {
        public long? FromIndex { get; set; }
        public long? ToIndex { get; set; }

        public EntityType? FromType { get; set; }
        public EntityType? ToType { get; set; }

        public Relation()
        {
            Type = EntityType.Relation;
        }
    }
}
