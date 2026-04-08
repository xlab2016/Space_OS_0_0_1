using System.Collections.Generic;

namespace Magic.Kernel
{
    /// <summary>
    /// Кадр памяти на один вызов (Call). <see cref="Global"/> — та же ссылка, что <see cref="MemoryContext.Global"/>, общая для всех кадров.
    /// <see cref="Local"/> — изолированные слоты этого вызова. <see cref="Inherited"/> — опционально (например снимок для вложенных сценариев).
    /// </summary>
    public sealed class MemoryBlock
    {
        internal MemoryBlock(MemoryContext owner) => Owner = owner;

        internal MemoryContext Owner { get; }

        /// <summary>Общая глобальная память (один словарь на весь <see cref="MemoryContext"/>).</summary>
        public Dictionary<long, object> Global => Owner.Global;

        public Dictionary<long, object> Local { get; } = new Dictionary<long, object>();

        /// <summary>Дополнительный слой ниже <see cref="Local"/> при разрешении адреса (обычно пустой).</summary>
        public Dictionary<long, object> Inherited { get; } = new Dictionary<long, object>();
    }
}
