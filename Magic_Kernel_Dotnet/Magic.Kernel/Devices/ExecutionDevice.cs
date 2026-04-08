using System.Collections.Generic;
using System.Threading.Tasks;
using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Types;

namespace Magic.Kernel.Devices
{
    /// <summary>
    /// AGI builtin <c>execution</c>: текущее устройство исполнения (интерпретатор + загруженный <see cref="ExecutableUnit"/>).
    /// Свойство <see cref="Objects"/> — снимок <see cref="ExecutableUnit.Objects"/> как <see cref="DefList"/>.
    /// </summary>
    public sealed class ExecutionDevice : IDefType
    {
        private readonly ExecutableUnit? _unit;

        public ExecutionDevice(ExecutableUnit? unit)
        {
            _unit = unit;
            Name = "execution";
        }

        public long? Index { get; set; }
        public string Name { get; set; }
        public StructurePosition? Position { get; set; }
        public List<IDefType> Generalizations { get; set; } = new List<IDefType>();

        /// <summary>Снимок объектов юнита для AGI <c>execution.objects</c> / <c>objects()</c>.</summary>
        public DefList Objects => BuildObjectsList();

        private DefList BuildObjectsList()
        {
            var list = new DefList { Name = "objects" };
            if (_unit?.Objects == null)
                return list;
            foreach (var o in _unit.Objects)
                list.Items.Add(o);
            return list;
        }

        public Task<object?> Await() => Task.FromResult<object?>(this);

        public Task<object?> AwaitObjAsync() => Task.FromResult<object?>(this);

        public Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            switch (methodName?.Trim().ToLowerInvariant())
            {
                case "objects":
                case "getobjects":
                    return Task.FromResult<object?>(BuildObjectsList());
                default:
                    throw new CallUnknownMethodException(methodName ?? "", this);
            }
        }

        public Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
            => Task.FromResult((true, (object?)null, (object?)null));
    }
}
