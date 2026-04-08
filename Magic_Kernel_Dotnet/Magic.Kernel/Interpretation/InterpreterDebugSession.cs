using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Magic.Kernel.Interpretation;

public enum DebugResumeAction
{
    Run,
    /// <summary>Следующая другая строка исходника (SourceLine &gt; 0).</summary>
    StepOverLine,
    /// <summary>Одна машинная инструкция.</summary>
    StepInstruction,
    /// <summary>
    /// С вкладки Code: на той же строке AGI не останавливаться между инструкциями, пока не встретится вызов (Call/ACall/CallObj);
    /// выполнить его и остановиться на первой инструкции тела (как «зайти» в процедуру/функцию).
    /// </summary>
    StepInto,
    Stop
}

/// <summary>Паузы отладчика AGI (1-based номера строк).</summary>
public sealed class InterpreterDebugSession
{
    /// <summary>
    /// Одно ожидание = один TCS: повторный сигнал на уже завершённый wait ничего не делает.
    /// У <see cref="SemaphoreSlim"/> лишний Release давал «orphan permit» — следующий WaitAsync сразу проходил,
    /// <see cref="Interpreter"/> вызывал Continue без клика и подавлял breakpoint через <c>_debugSkipSourceLine</c>.
    /// </summary>
    private TaskCompletionSource<bool>? _continueTcs;

    private CancellationTokenSource? _stopCts;
    private DebugResumeAction _pendingResume = DebugResumeAction.Run;

    private readonly object _breakpointLock = new();
    private readonly Dictionary<string, HashSet<int>> _agiBreakpointsByPath =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<int> _asmBreakpoints = new();

    /// <summary>AGI строка (1-based), строка листинга ASM (1-based, 0 если нет), путь к .agi.</summary>
    public event Action<int, int, string?>? PausedAtDebugLocation;

    /// <summary>Интерпретатор, остановившийся на паузе (для Memory/Stack в UI). Сбрасывается после выхода из ожидания.</summary>
    public Interpreter? PausedInterpreter { get; private set; }

    /// <summary>Копирует breakpoint по файлам .agi и строкам листинга ASM (1-based). Потокобезопасно.</summary>
    public void ReplaceBreakpointsFrom(
        IReadOnlyDictionary<string, HashSet<int>>? agiBreakpointsByPath,
        IEnumerable<int>? asmListingLines = null)
    {
        lock (_breakpointLock)
        {
            _agiBreakpointsByPath.Clear();
            _asmBreakpoints.Clear();
            if (agiBreakpointsByPath != null)
            {
                foreach (var kv in agiBreakpointsByPath)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null || kv.Value.Count == 0)
                        continue;
                    var key = Path.GetFullPath(kv.Key);
                    if (!_agiBreakpointsByPath.TryGetValue(key, out var set))
                    {
                        set = new HashSet<int>();
                        _agiBreakpointsByPath[key] = set;
                    }

                    foreach (var line in kv.Value)
                    {
                        if (line > 0)
                            set.Add(line);
                    }
                }
            }

            if (asmListingLines != null)
            {
                foreach (var line in asmListingLines)
                {
                    if (line > 0)
                        _asmBreakpoints.Add(line);
                }
            }
        }
    }

    public void BeginRun(CancellationToken externalToken = default)
    {
        _pendingResume = DebugResumeAction.Run;
        _continueTcs = null;

        _stopCts?.Dispose();
        _stopCts = externalToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
            : new CancellationTokenSource();
    }

    public CancellationToken ContinueCancellationToken => _stopCts?.Token ?? CancellationToken.None;

    public void EndRun()
    {
        try
        {
            _stopCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        ReleaseWaitCore();

        _stopCts?.Dispose();
        _stopCts = null;

        _pendingResume = DebugResumeAction.Run;
    }

    public bool IsBreakpointAgiLine(string? sourcePath, int sourceLine)
    {
        if (sourceLine <= 0 || string.IsNullOrWhiteSpace(sourcePath))
            return false;
        var key = Path.GetFullPath(sourcePath);
        lock (_breakpointLock)
            return _agiBreakpointsByPath.TryGetValue(key, out var set) && set.Contains(sourceLine);
    }

    public bool IsBreakpointAsmLine(int asmListingLine)
    {
        lock (_breakpointLock)
            return asmListingLine > 0 && _asmBreakpoints.Contains(asmListingLine);
    }

    public async Task WaitPausedAsync(Interpreter? pausedInterpreter, int sourceLine, int asmListingLine, string? agiSourcePath, CancellationToken cancellationToken = default)
    {
        PausedInterpreter = pausedInterpreter;
        try
        {
            PausedAtDebugLocation?.Invoke(sourceLine, asmListingLine, agiSourcePath);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _continueTcs = tcs;

            using (cancellationToken.Register(() => tcs.TrySetResult(true)))
            {
                await tcs.Task.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            PausedInterpreter = null;
        }
    }

    public DebugResumeAction ConsumeResumeAction()
    {
        var a = _pendingResume;
        _pendingResume = DebugResumeAction.Run;
        return a;
    }

    public void RequestContinue()
    {
        _pendingResume = DebugResumeAction.Run;
        ReleaseWaitCore();
    }

    public void RequestStepOverLine()
    {
        _pendingResume = DebugResumeAction.StepOverLine;
        ReleaseWaitCore();
    }

    public void RequestStepInstruction()
    {
        _pendingResume = DebugResumeAction.StepInstruction;
        ReleaseWaitCore();
    }

    /// <summary>F11 на вкладке Code: заход в вызов без пошагового прохода пролога строки.</summary>
    public void RequestStepInto()
    {
        _pendingResume = DebugResumeAction.StepInto;
        ReleaseWaitCore();
    }

    public void RequestStop()
    {
        _pendingResume = DebugResumeAction.Stop;
        try
        {
            _stopCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        ReleaseWaitCore();
    }

    private void ReleaseWaitCore()
        => _continueTcs?.TrySetResult(true);
}
