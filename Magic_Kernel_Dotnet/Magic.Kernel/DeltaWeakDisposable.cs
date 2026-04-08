using System;
using System.Collections;
using Magic.Kernel.Devices.Streams.Drivers;

namespace Magic.Kernel
{
    /// <summary>
    /// Lightweight disposable contract used by runtime memory to release
    /// heavy deltas without relying on full IDisposable lifetimes.
    /// </summary>
    public interface IWeakDisposable
    {
        void WeakDispose();
    }

    /// <summary>
    /// Wraps a delta object (typically a dictionary with references to external
    /// resources such as files or network handles) and provides best-effort
    /// clean-up when the owning memory cell is overwritten or cleared.
    /// </summary>
    public sealed class DeltaWeakDisposable : IWeakDisposable
    {
        private object? _value;

        public DeltaWeakDisposable(object? value)
        {
            _value = value;
        }

        public object? Value => _value;

        public void WeakDispose()
        {
            try
            {
                if (_value is IWeakDisposable weak)
                {
                    weak.WeakDispose();
                }
                else if (_value is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                else if (_value is IDictionary dict)
                {
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (entry.Value is IWeakDisposable weakChild)
                        {
                            weakChild.WeakDispose();
                        }
                        else if (entry.Value is IDisposable dispChild)
                        {
                            dispChild.Dispose();
                        }

                        // Break strong references from the dictionary to allow GC.
                        dict[entry.Key] = null;
                    }

                    dict.Clear();
                }
                else if (_value is IEnumerable enumerable && _value is not string)
                {
                    foreach (var item in enumerable)
                    {
                        if (item is IWeakDisposable weakChild)
                        {
                            weakChild.WeakDispose();
                        }
                        else if (item is IDisposable dispChild)
                        {
                            dispChild.Dispose();
                        }
                    }
                }
            }
            catch
            {
                // best-effort cleanup; ignore secondary errors
            }
            finally
            {
                _value = null;
            }
        }
    }
}

