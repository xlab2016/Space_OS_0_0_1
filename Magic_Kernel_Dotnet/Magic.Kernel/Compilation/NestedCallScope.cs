using System.Collections.Generic;

namespace Magic.Kernel.Compilation
{
    /// <summary>Лексическая цепочка вложенных подпрограмм: короткое имя вызова → манглированное имя в <see cref="ExecutableUnit.Procedures"/> или <see cref="ExecutableUnit.Functions"/>.</summary>
    internal sealed class NestedCallScope
    {
        private readonly Dictionary<string, string> _localNameToMangled;

        public NestedCallScope(NestedCallScope? parent, Dictionary<string, string> localNameToMangled)
        {
            Parent = parent;
            _localNameToMangled = localNameToMangled;
        }

        public NestedCallScope? Parent { get; }

        public bool TryResolve(string name, out string mangled)
        {
            if (_localNameToMangled.TryGetValue(name, out mangled!))
                return true;
            return Parent != null && Parent.TryResolve(name, out mangled);
        }
    }
}
