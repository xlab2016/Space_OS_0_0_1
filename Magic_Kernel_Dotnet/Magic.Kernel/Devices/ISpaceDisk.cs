using Magic.Kernel.Space;
using Magic.Kernel.Devices.SSC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public interface ISpaceDisk : IDevice, ICapableDevice
    {
        public SpaceDiskConfiguration Configuration { get; set; }

        public Task<SpaceOperationResult> AddVertex(Vertex vertex, string? spaceName);
        public Task<SpaceOperationResult> AddRelation(Relation relation, string? spaceName);
        public Task<SpaceOperationResult> AddShape(Shape shape, string? spaceName);
        public Task<Vertex?> GetVertex(long? index, string? name, string? spaceName);
        public Task<Relation?> GetRelation(long? index, string? name, string? spaceName);
        public Task<Shape?> GetShape(long? index, string? name, string? spaceName);
        public Task<SSCResult> CompileAsync(IStreamDevice device, ISSCompiler compiler);
    }
}
