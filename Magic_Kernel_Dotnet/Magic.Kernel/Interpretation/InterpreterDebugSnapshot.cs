using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Magic.Kernel.Compilation;
using Magic.Kernel.Core;
using Magic.Kernel.Data;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Store;
using Magic.Kernel.Devices.Streams;
using Magic.Kernel.Types;

namespace Magic.Kernel.Interpretation;

/// <summary>Снимок стека вычислений, вызовов и памяти для панели отладчика UI.</summary>
public sealed class InterpreterDebugSnapshot
{
    public string? UnitDisplay { get; init; }
    public long InstructionPointer { get; init; }
    /// <summary>Глубина стека <see cref="MemoryContext.CallFrameDepth"/> (кадры локальной памяти на вызов).</summary>
    public int MemoryCallFrameDepth { get; set; }
    public List<InterpreterDebugStackSlot> EvaluationStack { get; } = new();
    public List<InterpreterDebugCallFrame> CallStack { get; } = new();
    public List<InterpreterDebugMemoryLayer> MemoryLayers { get; } = new();
}

public sealed class InterpreterDebugStackSlot
{
    public int Index { get; init; }
    public string TypeName { get; init; } = "";
    public string Value { get; init; } = "";
    /// <summary>Дерево для раскрытия в RowDetails; null если скаляр короткий.</summary>
    public InterpreterInspectNode? InspectRoot { get; init; }
}

public sealed class InterpreterDebugCallFrame
{
    public int Depth { get; init; }
    public string? Name { get; init; }
    public long ReturnIp { get; init; }
}

public sealed class InterpreterDebugMemoryLayer
{
    public string Name { get; init; } = "";
    public List<InterpreterDebugMemoryCell> Cells { get; } = new();
}

public sealed class InterpreterDebugMemoryCell
{
    public long Slot { get; init; }
    public string TypeName { get; init; } = "";
    public string Value { get; init; } = "";
    public InterpreterInspectNode? InspectRoot { get; init; }
}

/// <summary>Двоичный payload для окна «Просмотреть» в отладчике (без копирования гигабайтов в Detail).</summary>
public sealed class BinaryDebugPayload
{
    public BinaryDebugPayload(ReadOnlyMemory<byte> data) => Data = data;

    public ReadOnlyMemory<byte> Data { get; }
}

/// <summary>Узел дерева в панели отладчика (TreeView).</summary>
public sealed class InterpreterInspectNode : INotifyPropertyChanged
{
    public const string SensitiveMaskedDisplay = "••••••••";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string _caption = "";
    public string Caption
    {
        get => _caption;
        set
        {
            if (_caption == value) return;
            _caption = value ?? "";
            Notify();
        }
    }

    private string? _detail;
    public string? Detail
    {
        get => _detail;
        set
        {
            if (_detail == value) return;
            _detail = value;
            Notify();
            Notify(nameof(ShowCopyButton));
        }
    }

    public List<InterpreterInspectNode> Children { get; } = new();

    /// <summary>Скаляр с чувствительным именем поля; значение в <see cref="SensitivePlainValue"/>, в UI по умолчанию маска.</summary>
    public bool IsSensitiveLeaf { get; internal set; }

    public string? SensitivePlainValue { get; internal set; }

    private bool _sensitiveRevealed;

    public string SensitiveRevealCaption => _sensitiveRevealed ? "Скрыть" : "Показать";

    private BinaryDebugPayload? _binaryPayload;

    /// <summary>Если задан — в UI показывается «Просмотреть»; полные байты не подставляются в <see cref="Detail"/>.</summary>
    public BinaryDebugPayload? BinaryPayload
    {
        get => _binaryPayload;
        set
        {
            if (_binaryPayload == value) return;
            _binaryPayload = value;
            Notify();
            Notify(nameof(ShowViewPayloadButton));
            Notify(nameof(ShowCopyButton));
        }
    }

    public bool ShowViewPayloadButton => BinaryPayload != null;

    public bool ShowCopyButton =>
        BinaryPayload == null && Children.Count == 0 && !string.IsNullOrWhiteSpace(GetCopyableText());

    public void ToggleSensitiveReveal()
    {
        if (!IsSensitiveLeaf)
            return;
        _sensitiveRevealed = !_sensitiveRevealed;
        Detail = _sensitiveRevealed ? SensitivePlainValue : SensitiveMaskedDisplay;
        Notify(nameof(SensitiveRevealCaption));
    }

    /// <summary>Текст для буфера: для sensitive всегда исходное значение.</summary>
    public string? GetCopyableText()
    {
        if (BinaryPayload != null)
            return null;
        if (Children.Count > 0)
            return null;
        if (IsSensitiveLeaf && SensitivePlainValue != null)
            return SensitivePlainValue;
        if (!string.IsNullOrEmpty(Detail))
            return Detail;
        if (!string.IsNullOrEmpty(Caption))
            return Caption;
        return null;
    }

    internal static InterpreterInspectNode CreateSensitiveScalar(string caption, string plainDetail)
    {
        var n = new InterpreterInspectNode
        {
            IsSensitiveLeaf = true,
            SensitivePlainValue = plainDetail
        };
        n._caption = caption;
        n._detail = SensitiveMaskedDisplay;
        return n;
    }
}

internal static class InterpreterDebugFormatting
{
    internal const int GridSummaryMax = 120;
    internal const int TreeLeafMax = 800;

    private static readonly string[] SensitiveKeyMarkers =
    [
        "password", "secret", "token", "key", "credential", "jwt", "auth"
    ];

    internal static bool IsSensitiveKeyName(string name)
        => !string.IsNullOrEmpty(name) &&
           SensitiveKeyMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    internal static string TrimForGrid(string? s, int max = GridSummaryMax)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "…";
    }

    internal static string TrimForTree(string? s, int max = TreeLeafMax)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "…";
    }

    /// <summary>Краткая строка для грида/дерева без разворачивания всего массива.</summary>
    internal static string FormatBinaryPreviewSummary(ReadOnlySpan<byte> data, int maxBytes = 48)
    {
        if (data.IsEmpty) return "0 bytes";
        var take = Math.Min(maxBytes, data.Length);
        var hx = Convert.ToHexString(data[..take]);
        return data.Length > take
            ? $"{data.Length} bytes · {hx}…"
            : $"{data.Length} bytes · {hx}";
    }

    internal static string FormatTypeLabel(object? v)
    {
        if (v == null) return "null";
        if (v is DefList dl) return $"list ({dl.Items.Count})";
        if (v is Vault vault) return string.IsNullOrWhiteSpace(vault.Name) ? "vault" : $"vault ({vault.Name})";
        if (v is JsonElement je) return $"json:{je.ValueKind}";
        if (v is IStreamChunk ch)
        {
            var n = ch.Data?.Length ?? 0;
            return $"StreamChunk ({n} B)";
        }

        if (v is byte[] ba)
            return $"byte[] ({ba.Length})";
        return v.GetType().Name;
    }

    /// <summary>Строки не отслеживаем; value-type по ссылке не сливаем (кроме упаковки — это объект).</summary>
    private static bool ShouldTrackReferenceCycle(object o)
    {
        if (o is string)
            return false;
        return !o.GetType().IsValueType;
    }

    /// <summary>Одна строка для ячейки грида.</summary>
    internal static string FormatValueSummary(object? o)
    {
        var node = BuildInspectTree(o, 0, null);
        if (node.Children.Count == 0)
            return TrimForGrid(string.IsNullOrEmpty(node.Detail) ? node.Caption : node.Detail);
        var head = string.IsNullOrWhiteSpace(node.Caption)
            ? $"{node.Children.Count} items"
            : $"{node.Caption} · {node.Children.Count}…";
        return TrimForGrid(head);
    }

    internal static InterpreterInspectNode? TryBuildInspectRoot(object? o)
    {
        if (o is Vault or DefList or DefStream or IStreamChunk)
            return BuildInspectTree(o, 0, null);

        var n = BuildInspectTree(o, 0, null);
        if (n.Children.Count > 0)
            return n;
        if (!string.IsNullOrEmpty(n.Detail) && n.Detail.Length > GridSummaryMax)
            return n;
        return null;
    }

    internal static string FormatValue(object? o) => FormatValueSummary(o);

    internal static InterpreterInspectNode BuildInspectTree(object? o, int depth, HashSet<object>? expandedRefs)
    {
        const int maxDepth = 14;
        const int maxItems = 240;
        if (depth > maxDepth)
            return Leaf("…", "max depth");

        expandedRefs ??= new HashSet<object>(ReferenceEqualityComparer.Instance);

        if (o != null && ShouldTrackReferenceCycle(o) && !expandedRefs.Add(o))
            return Leaf("↺", $"{o.GetType().Name} · уже показано (цикл)");

        switch (o)
        {
            case null:
                return Leaf("null", "");
            case Vault vault:
                return BuildVaultTree(vault, depth, maxItems, expandedRefs);
            case DefList dl:
                return BuildDefListTree(dl, depth, maxItems, expandedRefs);
            case DefStream stream:
                return BuildDefStreamTree(stream, depth, maxItems, expandedRefs);
            case IStreamChunk sch:
                return BuildStreamChunkInspectTree(sch, depth, maxItems, expandedRefs);
            case string s:
                return BuildStringTree(s, depth, maxItems, expandedRefs);
            case JsonElement je:
                return BuildJsonElementTree(je, depth, maxItems, expandedRefs);
            case bool b:
                return Leaf("", b ? "true" : "false");
            case char c:
                return Leaf("", "'" + c + "'");
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return Leaf("", Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "");
            case DeltaWeakDisposable dwd:
                return BuildDeltaWeakDisposableTree(dwd, depth, maxItems, expandedRefs);
            case Table tbl:
                return BuildTableInspectTree(tbl, depth, maxItems, expandedRefs);
            case Database dbc:
                return BuildDatabaseInspectTree(dbc, depth, maxItems, expandedRefs);
            case DatabaseDevice dbDev:
                return BuildDatabaseDeviceInspectTree(dbDev, depth, maxItems, expandedRefs);
            case DefType defType:
                return BuildDefTypeInspectTree(defType, depth, maxItems, expandedRefs);
        }

        if (o is byte[] barr)
            return BuildByteArrayInspectNode(barr);

        if (o is IDictionary dict)
            return BuildDictionaryTree(dict, depth, maxItems, expandedRefs);

        if (o is IList list)
            return BuildListTree(list, depth, maxItems, expandedRefs);

        if (o is IEnumerable en && o is not string)
            return BuildEnumerableTree(en, depth, maxItems, expandedRefs);

        if (o is IDefType def)
        {
            var nm = string.IsNullOrWhiteSpace(def.Name) ? o.GetType().Name : def.Name;
            var ix = def.Index;
            var d = ix.HasValue ? $"{nm} index={ix}" : nm;
            return Leaf("", d);
        }

        var t = o.ToString() ?? "";
        return Leaf("", TrimForTree(t, TreeLeafMax));
    }

    private static InterpreterInspectNode BuildByteArrayInspectNode(byte[] barr)
    {
        return new InterpreterInspectNode
        {
            Caption = "byte[]",
            Detail = FormatBinaryPreviewSummary(barr, maxBytes: 64),
            BinaryPayload = new BinaryDebugPayload(barr)
        };
    }

    private static InterpreterInspectNode BuildStreamChunkInspectTree(IStreamChunk chunk, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var data = chunk.Data ?? Array.Empty<byte>();
        var root = new InterpreterInspectNode
        {
            Caption = "StreamChunk",
            Detail = $"{data.Length} B · {chunk.DataFormat} · {chunk.ApplicationFormat}"
        };
        root.Children.Add(NamedChild(nameof(IStreamChunk.ChunkSize), chunk.ChunkSize, depth + 1, expandedRefs));
        root.Children.Add(NamedChild(nameof(IStreamChunk.DataFormat), chunk.DataFormat.ToString(), depth + 1, expandedRefs));
        root.Children.Add(NamedChild(nameof(IStreamChunk.ApplicationFormat), chunk.ApplicationFormat.ToString(), depth + 1, expandedRefs));
        var posText = FormatStructurePosition(chunk.Position);
        if (!string.IsNullOrEmpty(posText))
            root.Children.Add(NamedChild(nameof(IStreamChunk.Position), posText, depth + 1, expandedRefs));

        root.Children.Add(new InterpreterInspectNode
        {
            Caption = nameof(IStreamChunk.Data),
            Detail = FormatBinaryPreviewSummary(data, maxBytes: 48),
            BinaryPayload = new BinaryDebugPayload(data)
        });
        return root;
    }

    private static InterpreterInspectNode BuildDeltaWeakDisposableTree(DeltaWeakDisposable dwd, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var root = new InterpreterInspectNode
        {
            Caption = nameof(DeltaWeakDisposable),
            Detail = dwd.Value == null ? "(empty)" : dwd.Value.GetType().Name
        };
        root.Children.Add(NamedChild(nameof(DeltaWeakDisposable.Value), dwd.Value, depth + 1, expandedRefs));
        return root;
    }

    private static InterpreterInspectNode BuildDefTypeInspectTree(DefType dt, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var typeLabel = dt.GetType().Name;
        var root = new InterpreterInspectNode
        {
            Caption = typeLabel,
            Detail = TrimForTree(string.IsNullOrWhiteSpace(dt.FullName) ? dt.Name : dt.FullName, TreeLeafMax)
        };

        void AddChild(string label, object? value)
        {
            if (root.Children.Count >= maxItems)
            {
                if (root.Children[^1].Caption != "…")
                    root.Children.Add(Leaf("…", "omitted"));
                return;
            }

            root.Children.Add(NamedChild(label, value, depth + 1, expandedRefs));
        }

        if (!string.IsNullOrWhiteSpace(dt.Name))
            AddChild(nameof(DefType.Name), dt.Name);
        if (!string.IsNullOrWhiteSpace(dt.Namespace))
            AddChild(nameof(DefType.Namespace), dt.Namespace);
        if (!string.IsNullOrWhiteSpace(dt.FullName) && !string.Equals(dt.FullName, dt.Name, StringComparison.Ordinal))
            AddChild(nameof(DefType.FullName), dt.FullName);
        if (dt.Index.HasValue)
            AddChild(nameof(DefType.Index), dt.Index.Value);

        var posText = FormatStructurePosition(dt.Position);
        if (!string.IsNullOrEmpty(posText))
            AddChild(nameof(DefType.Position), posText);

        if (dt.Fields is { Count: > 0 })
        {
            var fi = 0;
            foreach (var f in dt.Fields)
            {
                if (root.Children.Count >= maxItems) break;
                AddChild($"field:{f.Name}", f.ToString());
                if (++fi > 64) { AddChild("…", "more fields omitted"); break; }
            }
        }

        if (dt.Methods is { Count: > 0 })
        {
            var mi = 0;
            foreach (var m in dt.Methods)
            {
                if (root.Children.Count >= maxItems) break;
                AddChild($"method:{m.Name}", m.ToString());
                if (++mi > 64) { AddChild("…", "more methods omitted"); break; }
            }
        }

        if (dt.Generalizations is { Count: > 0 })
        {
            for (var i = 0; i < dt.Generalizations.Count; i++)
            {
                if (root.Children.Count >= maxItems) break;
                var g = dt.Generalizations[i];
                root.Children.Add(NamedChild($"generalization[{i}]", g, depth + 1, expandedRefs));
            }
        }

        if (dt is Column col)
        {
            if (!string.IsNullOrWhiteSpace(col.Type))
                AddChild(nameof(Column.Type), col.Type);
            if (col.Modifiers is { Count: > 0 })
                root.Children.Add(NamedChild(nameof(Column.Modifiers), col.Modifiers, depth + 1, expandedRefs));
        }

        if (root.Children.Count == 0)
            root.Children.Add(Leaf("type", dt.GetType().FullName ?? dt.GetType().Name));

        return root;
    }

    private static InterpreterInspectNode BuildTableInspectTree(Table t, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var root = BuildDefTypeInspectTree(t, depth, maxItems, expandedRefs);
        root.Caption = "Table";
        if (t.Database != null)
            root.Children.Add(NamedChild(nameof(Table.Database), t.Database, depth + 1, expandedRefs));
        if (t.Columns is { Count: > 0 })
        {
            var cols = new InterpreterInspectNode { Caption = nameof(Table.Columns), Detail = $"{t.Columns.Count} columns" };
            for (var i = 0; i < t.Columns.Count && cols.Children.Count < maxItems; i++)
            {
                var c = t.Columns[i];
                cols.Children.Add(NamedChild(string.IsNullOrWhiteSpace(c.Name) ? $"[{i}]" : c.Name, c, depth + 2, expandedRefs));
            }

            if (t.Columns.Count > cols.Children.Count)
                cols.Children.Add(Leaf("…", "more columns omitted"));
            root.Children.Add(cols);
        }

        var pendingRoot = new InterpreterInspectNode
        {
            Caption = nameof(Table.PendingRows),
            Detail = $"{t.PendingRows.Count} row(s)"
        };
        var rowCap = Math.Min(t.PendingRows.Count, 24);
        for (var r = 0; r < rowCap; r++)
            pendingRoot.Children.Add(NamedChild($"row[{r}]", t.PendingRows[r], depth + 2, expandedRefs));
        if (t.PendingRows.Count > rowCap)
            pendingRoot.Children.Add(Leaf("…", $"{t.PendingRows.Count - rowCap} more rows omitted"));
        root.Children.Add(pendingRoot);

        if (t.FilterExpr != null)
            root.Children.Add(NamedChild(nameof(Table.FilterExpr), t.FilterExpr.ToString(), depth + 1, expandedRefs));

        return root;
    }

    private static InterpreterInspectNode BuildDatabaseInspectTree(Database d, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var root = BuildDefTypeInspectTree(d, depth, maxItems, expandedRefs);
        root.Caption = "Database";
        if (d.Device != null)
            root.Children.Add(NamedChild(nameof(Database.Device), d.Device, depth + 1, expandedRefs));
        if (d.Tables is { Count: > 0 })
        {
            var tblRoot = new InterpreterInspectNode { Caption = nameof(Database.Tables), Detail = $"{d.Tables.Count} table(s)" };
            for (var i = 0; i < d.Tables.Count && tblRoot.Children.Count < maxItems; i++)
            {
                var tb = d.Tables[i];
                tblRoot.Children.Add(NamedChild(string.IsNullOrWhiteSpace(tb.Name) ? $"[{i}]" : tb.Name, tb, depth + 2, expandedRefs));
            }

            if (d.Tables.Count > tblRoot.Children.Count)
                tblRoot.Children.Add(Leaf("…", "more tables omitted"));
            root.Children.Add(tblRoot);
        }

        return root;
    }

    private static InterpreterInspectNode BuildDatabaseDeviceInspectTree(DatabaseDevice dev, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var root = BuildDefTypeInspectTree(dev, depth, maxItems, expandedRefs);
        root.Caption = nameof(DatabaseDevice);
        root.Detail = TrimForTree(string.IsNullOrWhiteSpace(dev.FullName) ? "runtime database device" : dev.FullName, TreeLeafMax);
        return root;
    }

    private static InterpreterInspectNode Leaf(string caption, string detail)
    {
        return new InterpreterInspectNode { Caption = caption, Detail = detail };
    }

    private static string FormatStructurePosition(StructurePosition? p)
    {
        if (p == null)
            return "";
        var parts = new List<string>();
        if (p.IndexNames is { Count: > 0 })
            parts.Add(string.Join("/", p.IndexNames));
        if (p.Indices is { Count: > 0 })
            parts.Add("i:" + string.Join(",", p.Indices));
        if (p.AbsolutePosition != 0)
            parts.Add($"abs={p.AbsolutePosition}");
        if (p.RelativeIndex != 0 || !string.IsNullOrEmpty(p.RelativeIndexName))
            parts.Add($"r={p.RelativeIndex} {p.RelativeIndexName}".Trim());
        return parts.Count == 0 ? "" : string.Join(" · ", parts);
    }

    private static InterpreterInspectNode BuildDefStreamTree(DefStream s, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var typeName = s.GetType().Name;
        var namePart = string.IsNullOrWhiteSpace(s.Name) ? "" : s.Name.Trim();
        var detail = !s.Index.HasValue
            ? (string.IsNullOrEmpty(namePart) ? "(stream)" : namePart)
            : (string.IsNullOrEmpty(namePart) ? $"index={s.Index}" : $"{namePart} · index={s.Index}");

        var root = new InterpreterInspectNode
        {
            Caption = typeName,
            Detail = TrimForTree(detail, TreeLeafMax)
        };

        void AddNamed(string label, object? value)
        {
            if (root.Children.Count >= maxItems)
            {
                if (root.Children.Count == 0 || root.Children[^1].Caption != "…")
                    root.Children.Add(Leaf("…", "omitted"));
                return;
            }

            root.Children.Add(NamedChild(label, value, depth + 1, expandedRefs));
        }

        AddNamed(nameof(DefStream.Name), s.Name ?? "");
        if (s.Index.HasValue)
            AddNamed(nameof(DefStream.Index), s.Index.Value);

        var posText = FormatStructurePosition(s.Position);
        if (!string.IsNullOrEmpty(posText))
            AddNamed(nameof(DefStream.Position), posText);

        if (s is ClawStreamDevice claw)
        {
            AddNamed(nameof(ClawStreamDevice.Port), claw.Port);
            AddNamed(nameof(ClawStreamDevice.IsListening), claw.IsListening);
            if (!string.IsNullOrEmpty(claw.UnitName))
                AddNamed(nameof(ClawStreamDevice.UnitName), claw.UnitName);
            if (claw.UnitInstanceIndex.HasValue)
                AddNamed(nameof(ClawStreamDevice.UnitInstanceIndex), claw.UnitInstanceIndex.Value);

            foreach (var kv in claw.Methods.GetBindingsSnapshot().OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (root.Children.Count >= maxItems)
                {
                    if (root.Children[^1].Caption != "…")
                        root.Children.Add(Leaf("…", "more methods omitted"));
                    break;
                }

                root.Children.Add(NamedChild($"method:{kv.Key}", kv.Value, depth + 1, expandedRefs));
            }
        }

        if (s.Generalizations is { Count: > 0 })
        {
            for (var i = 0; i < s.Generalizations.Count; i++)
            {
                if (root.Children.Count >= maxItems)
                {
                    if (root.Children[^1].Caption != "…")
                        root.Children.Add(Leaf("…", "more generalizations omitted"));
                    break;
                }

                var g = s.Generalizations[i];
                if (g == null)
                    continue;
                root.Children.Add(NamedChild($"generalization[{i}]", g, depth + 1, expandedRefs));
            }
        }

        return root;
    }

    private static InterpreterInspectNode BuildVaultTree(Vault vault, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var sections = vault.GetDebugSections();
        var root = new InterpreterInspectNode
        {
            Caption = string.IsNullOrWhiteSpace(vault.Name) ? "vault" : $"vault · {vault.Name}",
            Detail = sections.Count == 0
                ? "no matching vault.json block for this program/system/module"
                : $"{sections.Count} section(s)"
        };
        if (sections.Count == 0)
            return root;

        var n = 0;
        foreach (var sec in sections)
        {
            if (++n > maxItems) { root.Children.Add(Leaf("…", "more sections omitted")); break; }
            var secNode = new InterpreterInspectNode
            {
                Caption = $"{sec.Program} / {sec.System} / {sec.Module}",
                Detail = $"instance {sec.InstanceIndex} · {sec.Store.Count} keys"
            };
            var k = 0;
            foreach (var key in sec.Store.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (++k > maxItems) { secNode.Children.Add(Leaf("…", "more keys omitted")); break; }
                secNode.Children.Add(NamedChild(key, sec.Store[key], depth + 1, expandedRefs));
            }

            root.Children.Add(secNode);
        }

        return root;
    }

    private static InterpreterInspectNode NamedChild(string name, object? value, int depth, HashSet<object> expandedRefs)
    {
        var inner = BuildInspectTree(value, depth, expandedRefs);
        if (inner.Children.Count > 0)
        {
            var wrap = new InterpreterInspectNode { Caption = name };
            if (!string.IsNullOrWhiteSpace(inner.Caption))
                wrap.Detail = inner.Caption;
            foreach (var c in inner.Children)
                wrap.Children.Add(c);
            return wrap;
        }

        var plain = TrimForTree(inner.Detail ?? inner.Caption, TreeLeafMax);
        if (IsSensitiveKeyName(name))
            return InterpreterInspectNode.CreateSensitiveScalar(name, plain);

        var leaf = new InterpreterInspectNode
        {
            Caption = name,
            Detail = plain
        };
        return leaf;
    }

    private static InterpreterInspectNode BuildDefListTree(DefList dl, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var root = new InterpreterInspectNode
        {
            Caption = $"list ({dl.Items.Count} items)",
            Detail = ""
        };
        for (var i = 0; i < dl.Items.Count; i++)
        {
            if (root.Children.Count >= maxItems) { root.Children.Add(Leaf("…", "more items omitted")); break; }
            var item = dl.Items[i];
            var child = BuildInspectTree(item, depth + 1, expandedRefs);
            child.Caption = $"[{i}]" + (string.IsNullOrEmpty(child.Caption) ? "" : " " + child.Caption);
            root.Children.Add(child);
        }

        return root;
    }

    private static InterpreterInspectNode BuildStringTree(string s, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var t = s.TrimStart();
        if ((t.StartsWith("{", StringComparison.Ordinal) && t.EndsWith("}", StringComparison.Ordinal)) ||
            (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal)))
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                var root = BuildJsonElementTree(doc.RootElement, depth, maxItems, expandedRefs);
                root.Caption = string.IsNullOrEmpty(root.Caption) ? "json" : $"json · {root.Caption}";
                return root;
            }
            catch
            {
                /* fallthrough */
            }
        }

        return Leaf("string", TrimForTree(s, TreeLeafMax));
    }

    private static InterpreterInspectNode BuildJsonElementTree(JsonElement je, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        switch (je.ValueKind)
        {
            case JsonValueKind.Object:
                var props = je.EnumerateObject().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var obj = new InterpreterInspectNode { Caption = "object", Detail = $"{props.Count} props" };
                var c = 0;
                foreach (var p in props)
                {
                    if (++c > maxItems) { obj.Children.Add(Leaf("…", "more props omitted")); break; }
                    obj.Children.Add(NamedChild(p.Name, JsonToObject(p.Value), depth + 1, expandedRefs));
                }

                return obj;
            case JsonValueKind.Array:
                var arr = new InterpreterInspectNode { Caption = "array", Detail = $"{je.GetArrayLength()} items" };
                var i = 0;
                foreach (var el in je.EnumerateArray())
                {
                    if (i >= maxItems) { arr.Children.Add(Leaf("…", "more elements omitted")); break; }
                    var child = BuildJsonElementTree(el, depth + 1, maxItems, expandedRefs);
                    child.Caption = $"[{i++}]" + (string.IsNullOrEmpty(child.Caption) ? "" : " " + child.Caption);
                    arr.Children.Add(child);
                }

                return arr;
            case JsonValueKind.String:
                return Leaf("", TrimForTree(je.GetString() ?? "", TreeLeafMax));
            case JsonValueKind.Number:
                return Leaf("", je.GetRawText());
            case JsonValueKind.True:
                return Leaf("", "true");
            case JsonValueKind.False:
                return Leaf("", "false");
            case JsonValueKind.Null:
                return Leaf("null", "");
            default:
                return Leaf("", TrimForTree(je.GetRawText(), TreeLeafMax));
        }
    }

    private static object? JsonToObject(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: return e.GetString();
            case JsonValueKind.Number:
                if (e.TryGetInt64(out var l)) return l;
                if (e.TryGetDouble(out var d)) return d;
                return e.GetRawText();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            case JsonValueKind.Array:
            case JsonValueKind.Object:
                return e;
            default:
                return e.GetRawText();
        }
    }

    private static InterpreterInspectNode BuildDictionaryTree(IDictionary dict, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var root = new InterpreterInspectNode { Caption = "dictionary", Detail = $"{dict.Count} keys" };
        var n = 0;
        foreach (DictionaryEntry e in dict)
        {
            if (++n > maxItems) { root.Children.Add(Leaf("…", "more keys omitted")); break; }
            var key = FormatDictionaryKey(e.Key);
            root.Children.Add(NamedChild(key, e.Value, depth + 1, expandedRefs));
        }

        return root;
    }

    private static string FormatDictionaryKey(object? key)
    {
        if (key == null) return "null";
        if (key is string s) return s;
        return key.ToString() ?? "?";
    }

    private static InterpreterInspectNode BuildListTree(IList list, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var root = new InterpreterInspectNode { Caption = "list", Detail = $"{list.Count} items" };
        for (var i = 0; i < list.Count; i++)
        {
            if (root.Children.Count >= maxItems) { root.Children.Add(Leaf("…", "more items omitted")); break; }
            var child = BuildInspectTree(list[i], depth + 1, expandedRefs);
            child.Caption = $"[{i}]" + (string.IsNullOrEmpty(child.Caption) ? "" : " " + child.Caption);
            root.Children.Add(child);
        }

        return root;
    }

    private static InterpreterInspectNode BuildEnumerableTree(IEnumerable en, int depth, int maxItems, HashSet<object> expandedRefs)
    {
        var root = new InterpreterInspectNode { Caption = "items", Detail = "" };
        var i = 0;
        foreach (var item in en)
        {
            if (root.Children.Count >= maxItems) { root.Children.Add(Leaf("…", "more omitted")); break; }
            var child = BuildInspectTree(item, depth + 1, expandedRefs);
            child.Caption = $"[{i++}]" + (string.IsNullOrEmpty(child.Caption) ? "" : " " + child.Caption);
            root.Children.Add(child);
        }

        if (root.Children.Count == 0)
            root.Detail = "(empty)";
        return root;
    }

    internal static string UnitLabel(ExecutableUnit? u)
    {
        if (u == null)
            return "(no unit)";
        var n = u.Name ?? "?";
        var sys = u.System ?? "";
        var mod = u.Module ?? "";
        if (!string.IsNullOrEmpty(sys) || !string.IsNullOrEmpty(mod))
            return $"{sys}/{mod}/{n}";
        return n;
    }
}
