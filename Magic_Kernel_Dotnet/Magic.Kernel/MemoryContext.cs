using System;
using System.Collections.Generic;
using System.Linq;

namespace Magic.Kernel
{
    public sealed class MemoryContext
    {
        private readonly Stack<MemoryBlock> _frames = new Stack<MemoryBlock>();

        /// <summary>Глобальная память (прелюдия, entrypoint с <c>IsExecutingEntryPoint</c>, общие def).</summary>
        public Dictionary<long, object> Global { get; set; } = new Dictionary<long, object>();

        /// <summary>Наследование между задачами (spawn): между верхним <see cref="MemoryBlock.Local"/> и <see cref="Global"/>.</summary>
        public Dictionary<long, object> Inherited { get; set; } = new Dictionary<long, object>();

        /// <summary>Слоты вне стека кадров (лямбды без <c>InterpreteAsync</c>, тесты).</summary>
        private readonly Dictionary<long, object> _ambientLocal = new Dictionary<long, object>();

        /// <summary>Число активных call-кадров (глубина после <see cref="PushCallFrame"/>).</summary>
        public int CallFrameDepth => _frames.Count;

        /// <summary>Текущий локальный словарь: верхний кадр или <see cref="_ambientLocal"/>, если кадров нет.</summary>
        public Dictionary<long, object> Local =>
            _frames.Count > 0 ? _frames.Peek().Local : _ambientLocal;

        /// <summary>Верхний кадр или <c>null</c>.</summary>
        public MemoryBlock? CurrentFrame => _frames.Count > 0 ? _frames.Peek() : null;

        /// <summary>Обход кадров от старых к новым (внешние вызовы → текущий).</summary>
        public IEnumerable<MemoryBlock> WalkFramesBottomToTop() => _frames.Reverse();

        /// <summary>Все значения в <see cref="MemoryBlock.Local"/> / <see cref="MemoryBlock.Inherited"/> по кадрам + <see cref="_ambientLocal"/>.</summary>
        internal IEnumerable<object> EnumerateAllFrameLocalValues()
        {
            foreach (var block in WalkFramesBottomToTop())
            {
                foreach (var kv in block.Local)
                    yield return kv.Value;
                foreach (var kv in block.Inherited)
                    yield return kv.Value;
            }

            foreach (var kv in _ambientLocal)
                yield return kv.Value;
        }

        public void PushCallFrame() => _frames.Push(new MemoryBlock(this));

        /// <summary>Совместимое имя: один кадр на один вызов процедуры/функции.</summary>
        public void PushLocalScope() => PushCallFrame();

        public void PopCallFrameAndClear()
        {
            if (_frames.Count == 0)
                return;
            var block = _frames.Pop();
            DisposeAndClear(block.Local);
            DisposeAndClear(block.Inherited);
        }

        /// <summary>Совместимое имя.</summary>
        public void PopLocalScopeAndClear() => PopCallFrameAndClear();

        /// <summary>Снять все кадры (без очистки <see cref="Global"/> / <see cref="Inherited"/>).</summary>
        public void ClearCallFrames()
        {
            while (_frames.Count > 0)
                PopCallFrameAndClear();
        }

        /// <summary>Совместимое имя: раньше очищался только стек scope-трекинга.</summary>
        public void ClearLocalScopes() => ClearCallFrames();

        public void ClearAmbientLocal()
        {
            DisposeAndClear(_ambientLocal);
        }

        /// <summary>
        /// Снимок ячеек для дочерней runtime-задачи: <see cref="Inherited"/> родителя + кадры (Inherited кадра, затем Local — как при чтении в одном кадре) снизу вверх, верх перекрывает низ + ambient.
        /// Без слоя <see cref="Inherited"/> значения, оставшиеся только там после spawn предка, терялись (Memory[n] is not set).
        /// </summary>
        public Dictionary<long, object>? CaptureLocalsForSpawn()
        {
            if (_frames.Count == 0 && _ambientLocal.Count == 0 && Inherited.Count == 0)
                return null;

            var merged = new Dictionary<long, object>();

            foreach (var kv in Inherited)
                merged[kv.Key] = kv.Value;

            foreach (var block in _frames.Reverse())
            {
                foreach (var kv in block.Inherited)
                    merged[kv.Key] = kv.Value;
                foreach (var kv in block.Local)
                    merged[kv.Key] = kv.Value;
            }

            foreach (var kv in _ambientLocal)
                merged[kv.Key] = kv.Value;

            return merged.Count > 0 ? merged : null;
        }

        public bool TryRead(MemoryAddress memoryCell, bool isExecutingEntryPoint, out object? value)
        {
            value = null;
            if (!memoryCell.Index.HasValue)
                return false;

            var index = memoryCell.Index.Value;

            if (memoryCell.IsGlobal)
            {
                if (TryGetCurrentLocal(index, out value))
                    return true;

                if (Global.TryGetValue(index, out value))
                    return true;

                return false;
            }

            if (isExecutingEntryPoint)
                return Global.TryGetValue(index, out value);

            if (TryGetCurrentLocal(index, out value))
                return true;

            if (TryGetCurrentFrameInherited(index, out value))
                return true;

            if (Inherited.TryGetValue(index, out value))
                return true;

            if (Global.TryGetValue(index, out value))
                return true;

            return false;
        }

        public object Read(MemoryAddress memoryCell, bool isExecutingEntryPoint)
        {
            if (!memoryCell.Index.HasValue)
                throw new InvalidOperationException("Memory address index is not specified.");

            if (!TryRead(memoryCell, isExecutingEntryPoint, out var value))
                throw new InvalidOperationException($"Memory[{memoryCell.Index.Value}] is not set.");

            return value!;
        }

        public void Write(MemoryAddress memoryCell, object? value, bool isExecutingEntryPoint, bool hasRuntime)
        {
            if (!memoryCell.Index.HasValue)
                throw new InvalidOperationException("Memory address index is not specified.");

            var index = memoryCell.Index.Value;
            var writeToLocal = !memoryCell.IsGlobal && !isExecutingEntryPoint && hasRuntime;
            Dictionary<long, object> target;
            if (memoryCell.IsGlobal || isExecutingEntryPoint || !hasRuntime)
                target = Global;
            else
                target = Local;

            if (target.TryGetValue(index, out var oldVal))
            {
                if (oldVal is IWeakDisposable weak)
                {
                    try { weak.WeakDispose(); } catch { /* best-effort */ }
                }

                if (oldVal is IDisposable d)
                {
                    try { d.Dispose(); } catch { /* best-effort */ }
                }
            }

            target[index] = value!;
        }

        private bool TryGetCurrentLocal(long index, out object? value)
        {
            if (_frames.Count > 0)
                return _frames.Peek().Local.TryGetValue(index, out value);
            return _ambientLocal.TryGetValue(index, out value);
        }

        private bool TryGetCurrentFrameInherited(long index, out object? value)
        {
            value = null;
            if (_frames.Count == 0)
                return false;
            return _frames.Peek().Inherited.TryGetValue(index, out value);
        }

        private static void DisposeAndClear(Dictionary<long, object> dict)
        {
            foreach (var index in dict.Keys.ToList())
            {
                if (!dict.TryGetValue(index, out var oldVal))
                    continue;

                if (oldVal is IWeakDisposable weak)
                {
                    try { weak.WeakDispose(); } catch { /* best-effort */ }
                }

                if (oldVal is IDisposable d)
                {
                    try { d.Dispose(); } catch { /* best-effort */ }
                }

                dict.Remove(index);
            }
        }
    }
}
