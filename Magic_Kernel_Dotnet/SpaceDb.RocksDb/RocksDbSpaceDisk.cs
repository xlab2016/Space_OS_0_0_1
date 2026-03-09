using Magic.Kernel;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.SSC;
using Magic.Kernel.Space;
using System.Text.Json;

namespace SpaceDb.Services
{
    public class RocksDbSpaceDisk : ISpaceDisk
    {
        private readonly IRocksDbService rocksDbService;

        public long? Index { get; set; }
        public string Name { get; set; }
        public SpaceDiskConfiguration Configuration { get; set; } = new SpaceDiskConfiguration();

        public RocksDbSpaceDisk(IRocksDbService rocksDbService)
        {
            this.rocksDbService = rocksDbService;
        }

        private static string KeyPrefix(string? spaceName) => string.IsNullOrEmpty(spaceName) ? "default" : spaceName;

        public async Task<SpaceOperationResult> AddVertex(Vertex vertex, string? spaceName)
        {
            var index = vertex.Index;

            if (index == null)
            {
                var newIndex = Configuration.VertexSequenceIndex ?? 0;
                Interlocked.Increment(ref newIndex);
                Configuration.VertexSequenceIndex = newIndex;
                vertex.Index = index = newIndex;
            }

            if (vertex.Position == null && Configuration.Projector != null)
            {
                var projector = Configuration.Projector;
                Position position;
                // TODO: quadrant
                var projectionResult = await projector.ProjectAsync(vertex, out position);

                if (!projectionResult.IsSuccess)
                {
                    return SpaceOperationResult.InnerDeviceFailure;
                }

                vertex.Position = position;
            }

            // TODO: serialize data to S3
            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            var prefix = KeyPrefix(spaceName);
            var result = await rocksDbService.PutJsonAsync($"{prefix}:vertices:index:{index}", vertex, jsonOptions);

            if (!result)
            {
                return SpaceOperationResult.InnerDeviceFailure;
            }

            if (!string.IsNullOrEmpty(vertex.Name))
            {
                if (vertex.Name.Length > 256)
                {
                    return SpaceOperationResult.NameLengthExceeded256;
                }

                result = await rocksDbService.PutJsonAsync($"{prefix}:vertices:name:{vertex.Name}", new EntityLink
                {
                    Index = index.Value,
                    Type = EntityType.Vertex
                }, jsonOptions);
                if (!result)
                {
                    return SpaceOperationResult.InnerDeviceFailure;
                }
            }

            return SpaceOperationResult.Success;
        }

        public async Task<SpaceOperationResult> AddRelation(Relation relation, string? spaceName)
        {
            var index = relation.Index;

            if (relation.FromIndex == null || relation.ToIndex == null || relation.FromType == null || relation.ToType == null)
            {
                return SpaceOperationResult.InvalidArguments;
            }

            if (index == null)
            {
                var newIndex = Configuration.RelationSequenceIndex ?? 0;
                Interlocked.Increment(ref newIndex);
                Configuration.RelationSequenceIndex = newIndex;
                relation.Index = index = newIndex;
            }

            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            var prefix = KeyPrefix(spaceName);
            var fromTypeKey = KernelSystem.ToString(relation.FromType.Value);
            var toTypeKey = KernelSystem.ToString(relation.ToType.Value);

            var inTypeKey = $"[{fromTypeKey},{toTypeKey}]";
            var outTypeKey = $"[{toTypeKey},{fromTypeKey}]";

            // Save both directions
            var innerDeviceResult = await rocksDbService.PutJsonAsync($"{prefix}:relations:index:{relation.Index}", relation, jsonOptions);

            if (!innerDeviceResult)
            {
                return SpaceOperationResult.InnerDeviceFailure;
            }

            var link = new EntityLink
            {
                Index = index.Value,
                Type = EntityType.Relation
            };

            innerDeviceResult = await rocksDbService.PutJsonAsync(
                $"{prefix}:relations:index:in:{inTypeKey}:[{relation.FromIndex.Value},{relation.ToIndex.Value}]", relation, jsonOptions);

            if (!innerDeviceResult)
            {
                return SpaceOperationResult.InnerDeviceFailure;
            }

            innerDeviceResult = await rocksDbService.PutJsonAsync(
                $"{prefix}:relations:index:out:{outTypeKey}:[{relation.ToIndex.Value},{relation.FromIndex.Value}]", relation, jsonOptions);

            if (!innerDeviceResult)
            {
                return SpaceOperationResult.InnerDeviceFailure;
            }

            if (!string.IsNullOrEmpty(relation.Name))
            {
                if (relation.Name.Length > 256)
                {
                    return SpaceOperationResult.NameLengthExceeded256;
                }

                innerDeviceResult = await rocksDbService.PutJsonAsync($"{prefix}:relations:name:{relation.Name}", new EntityLink
                {
                    Index = index.Value,
                    Type = EntityType.Relation
                }, jsonOptions);
                if (!innerDeviceResult)
                {
                    return SpaceOperationResult.InnerDeviceFailure;
                }
            }

            return SpaceOperationResult.Success;
        }

        public async Task<Vertex?> GetVertex(long? index, string? name, string? spaceName)
        {
            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            var prefix = KeyPrefix(spaceName);

            if (index.HasValue)
            {
                var vertex = await rocksDbService.GetJsonAsync<Vertex>($"{prefix}:vertices:index:{index.Value}", jsonOptions);
                return vertex;
            }

            if (!string.IsNullOrEmpty(name))
            {
                var link = await rocksDbService.GetJsonAsync<EntityLink>($"{prefix}:vertices:name:{name}", jsonOptions);
                if (link?.Index.HasValue == true)
                {
                    var vertex = await rocksDbService.GetJsonAsync<Vertex>($"{prefix}:vertices:index:{link.Index.Value}", jsonOptions);
                    return vertex;
                }
            }

            return null;
        }

        public async Task<SpaceOperationResult> AddShape(Shape shape, string? spaceName)
        {
            var index = shape.Index;

            if (index == null)
            {
                var newIndex = Configuration.ShapeSequenceIndex ?? 0;
                Interlocked.Increment(ref newIndex);
                Configuration.ShapeSequenceIndex = newIndex;
                shape.Index = index = newIndex;
            }

            if (shape.Origin == null && Configuration.Projector != null)
            {
                var projector = Configuration.Projector;
                List<Position> positions;
                // TODO: quadrant
                var projectionResult = await projector.ProjectAsync(shape, out positions);

                if (!projectionResult.IsSuccess)
                {
                    return SpaceOperationResult.InnerDeviceFailure;
                }

                shape.Origin = positions.FirstOrDefault();
            }

            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            var prefix = KeyPrefix(spaceName);
            var result = await rocksDbService.PutJsonAsync($"{prefix}:shapes:index:{index}", shape, jsonOptions);

            if (!result)
            {
                return SpaceOperationResult.InnerDeviceFailure;
            }

            if (!string.IsNullOrEmpty(shape.Name))
            {
                if (shape.Name.Length > 256)
                {
                    return SpaceOperationResult.NameLengthExceeded256;
                }

                result = await rocksDbService.PutJsonAsync($"{prefix}:shapes:name:{shape.Name}", new EntityLink
                {
                    Index = index.Value,
                    Type = EntityType.Shape
                }, jsonOptions);
                if (!result)
                {
                    return SpaceOperationResult.InnerDeviceFailure;
                }
            }

            return SpaceOperationResult.Success;
        }

        public async Task<Relation?> GetRelation(long? index, string? name, string? spaceName)
        {
            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            var prefix = KeyPrefix(spaceName);

            if (index.HasValue)
            {
                var relation = await rocksDbService.GetJsonAsync<Relation>($"{prefix}:relations:index:{index.Value}", jsonOptions);
                return relation;
            }

            if (!string.IsNullOrEmpty(name))
            {
                var link = await rocksDbService.GetJsonAsync<EntityLink>($"{prefix}:relations:name:{name}", jsonOptions);
                if (link?.Index.HasValue == true)
                {
                    var relation = await rocksDbService.GetJsonAsync<Relation>($"{prefix}:relations:index:{link.Index.Value}", jsonOptions);
                    return relation;
                }
            }

            return null;
        }

        public async Task<Shape?> GetShape(long? index, string? name, string? spaceName)
        {
            var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
            var prefix = KeyPrefix(spaceName);

            if (index.HasValue)
            {
                var shape = await rocksDbService.GetJsonAsync<Shape>($"{prefix}:shapes:index:{index.Value}", jsonOptions);
                return shape;
            }

            if (!string.IsNullOrEmpty(name))
            {
                var link = await rocksDbService.GetJsonAsync<EntityLink>($"{prefix}:shapes:name:{name}", jsonOptions);
                if (link?.Index.HasValue == true)
                {
                    var shape = await rocksDbService.GetJsonAsync<Shape>($"{prefix}:shapes:index:{link.Index.Value}", jsonOptions);
                    return shape;
                }
            }

            return null;
        }

        public async Task<bool> IsCapableAsync(IDevice other)
        {
            if (other is IProjectorDevice)
                return true;

            return false;
        }

        public async Task<SSCResult> CompileAsync(IStreamDevice device, ISSCompiler compiler)
        {
            if (compiler == null)
            {
                throw new ArgumentNullException(nameof(compiler));
            }

            var result = await compiler.CompileAsync(device, this).ConfigureAwait(false);
            return result ?? new SSCResult();
        }
    }
}

