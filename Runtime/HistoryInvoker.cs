using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace UniTaskCommandBus
{
    /// <summary>
    /// Command invoker with full history support: Undo/Redo/JumpTo/Pop/Clear and OnHistoryChanged event.
    /// Create via <see cref="CommandBus.Create{T}"/> builder — do not instantiate directly.
    /// </summary>
    public class HistoryInvoker<T> : Invoker<T>
    {
        private readonly int _maxSize;
        private readonly List<HistoryEntry<T>> _history = new List<HistoryEntry<T>>();
        private int _currentIndex = -1;
        private bool _suppressEvents;

        // Tracks the in-flight Undo/Redo TCS for Switch-style cancellation.
        private UniTaskCompletionSource<ExecutionResult> _undoRedoTcs;

        /// <summary>
        /// Fired whenever the history state changes (Execute, Undo, Redo, JumpTo, Pop, Clear).
        /// The three arguments are: action type, new pointer index, and name at that index.
        /// For Execute, this fires BEFORE the command lambda runs.
        /// </summary>
        public event Action<HistoryActionType, int, string> OnHistoryChanged;

        internal HistoryInvoker(AsyncPolicy policy, int maxSize) : base(policy)
        {
            _maxSize = maxSize;
        }

        // ── Read-only history state ──────────────────────────────────────────

        /// <summary>Number of entries currently in the history.</summary>
        public int HistoryCount { get { ThrowIfDisposed(); return _history.Count; } }

        /// <summary>Zero-based index of the currently active history entry, or -1 when empty.</summary>
        public int CurrentIndex { get { ThrowIfDisposed(); return _currentIndex; } }

        /// <summary>Returns the name snapshot stored at the given history index.</summary>
        /// <exception cref="ArgumentOutOfRangeException">index is out of range.</exception>
        public string GetName(int index)
        {
            ThrowIfDisposed();
            if (index < 0 || index >= _history.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _history[index].Name;
        }

        // ── Overrides ────────────────────────────────────────────────────────

        /// <summary>HistoryInvoker always passes ExecutionPhase.Execute to new commands.</summary>
        protected override ExecutionPhase GetExecutionPhase() => ExecutionPhase.Execute;

        /// <summary>
        /// Records the command in history and fires OnHistoryChanged BEFORE the lambda runs.
        /// Order: record → event → lambda (enforced by RunEntry in base class).
        /// </summary>
        private protected override void OnBeforeExecute(ICommand<T> cmd, T payload, string name)
        {
            // Branching: discard any "future" entries beyond current pointer.
            if (_currentIndex < _history.Count - 1)
                _history.RemoveRange(_currentIndex + 1, _history.Count - _currentIndex - 1);

            // Overflow: remove the oldest entry when at capacity.
            if (_history.Count >= _maxSize)
            {
                _history.RemoveAt(0);
                _currentIndex = Math.Max(_currentIndex - 1, -1);
            }

            _history.Add(new HistoryEntry<T>(cmd, payload, name));
            _currentIndex = _history.Count - 1;

            FireEvent(HistoryActionType.Execute, _currentIndex, name);
        }

        // ── Undo ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Undoes the command at the current pointer (fire-and-forget).
        /// Always uses Switch semantics: a running Undo/Redo is cancelled first.
        /// </summary>
        /// <exception cref="InvalidOperationException">No history to undo.</exception>
        public void Undo()
        {
            ThrowIfDisposed();
            UndoAsync().Forget();
        }

        /// <summary>
        /// Undoes the command at the current pointer and awaits completion.
        /// Always uses Switch semantics.
        /// </summary>
        public UniTask<ExecutionResult> UndoAsync()
        {
            ThrowIfDisposed();
            if (_currentIndex < 0)
                throw new InvalidOperationException("되돌릴 히스토리가 없습니다.");

            var entry = _history[_currentIndex];
            _currentIndex--;
            FireEvent(HistoryActionType.Undo, _currentIndex, NameAtIndex(_currentIndex));

            return StartUndoRedoTask(ct => entry.Command.InvokeUndo(entry.Payload, ct));
        }

        // ── Redo ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Redoes the next command (fire-and-forget).
        /// Always uses Switch semantics.
        /// </summary>
        /// <exception cref="InvalidOperationException">Already at the latest entry.</exception>
        public void Redo()
        {
            ThrowIfDisposed();
            RedoAsync().Forget();
        }

        /// <summary>
        /// Redoes the next command and awaits completion.
        /// Always uses Switch semantics.
        /// </summary>
        public UniTask<ExecutionResult> RedoAsync()
        {
            ThrowIfDisposed();
            if (_currentIndex >= _history.Count - 1)
                throw new InvalidOperationException("다시 실행할 히스토리가 없습니다.");

            _currentIndex++;
            var entry = _history[_currentIndex];
            FireEvent(HistoryActionType.Redo, _currentIndex, entry.Name);

            return StartUndoRedoTask(ct => entry.Command.InvokeExecute(entry.Payload, ExecutionPhase.Redo, ct));
        }

        // ── Pop ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Removes the last history entry when the pointer is at the latest position,
        /// or fires the event only when browsing. Throws on empty history.
        /// </summary>
        public void Pop()
        {
            ThrowIfDisposed();
            if (_history.Count == 0)
                throw new InvalidOperationException("히스토리가 비어 있습니다.");

            if (_currentIndex == _history.Count - 1)
            {
                // At latest: move pointer back and physically remove the entry.
                _history.RemoveAt(_history.Count - 1);
                _currentIndex--;
            }
            // Browsing: no pointer move, no list change — just fire the event below.

            FireEvent(HistoryActionType.Pop, _currentIndex, NameAtIndex(_currentIndex));
        }

        // ── Clear ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Clears all history entries and resets the pointer to -1.
        /// Does not cancel in-flight or queued commands.
        /// </summary>
        public void Clear()
        {
            ThrowIfDisposed();
            _history.Clear();
            _currentIndex = -1;
            FireEvent(HistoryActionType.Clear, -1, string.Empty);
        }

        // ── JumpTo ────────────────────────────────────────────────────────────

        /// <summary>
        /// Moves the history pointer to the target index by repeatedly calling Undo or Redo.
        /// Intermediate steps do not fire OnHistoryChanged; a single Jump event fires at the end.
        /// Returns synchronously; any async lambdas run fire-and-forget (last one wins via Switch).
        /// </summary>
        /// <exception cref="InvalidOperationException">Target equals the current index.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Target is outside valid range.</exception>
        public void JumpTo(int index)
        {
            ThrowIfDisposed();
            if (index == _currentIndex)
                throw new InvalidOperationException("현재 위치와 같은 인덱스로 점프할 수 없습니다.");
            if (index < 0 || index >= _history.Count)
                throw new ArgumentOutOfRangeException(nameof(index), "인덱스가 유효한 범위를 벗어났습니다.");

            _suppressEvents = true;
            try
            {
                while (_currentIndex > index) Undo();
                while (_currentIndex < index) Redo();
            }
            finally
            {
                _suppressEvents = false;
            }

            OnHistoryChanged?.Invoke(HistoryActionType.Jump, _currentIndex, NameAtIndex(_currentIndex));
        }

        // ── Dispose ──────────────────────────────────────────────────────────

        /// <summary>
        /// Cancels all work, clears history, and releases all OnHistoryChanged subscribers.
        /// After disposal any call throws <see cref="ObjectDisposedException"/>.
        /// </summary>
        public override void Dispose()
        {
            base.Dispose(); // CancelAll() + _disposed = true + _cts.Dispose()
            _history.Clear();
            _currentIndex = -1;
            OnHistoryChanged = null;
        }

        // ── Internal helpers ─────────────────────────────────────────────────

        private void FireEvent(HistoryActionType action, int index, string name)
        {
            if (!_suppressEvents)
                OnHistoryChanged?.Invoke(action, index, name);
        }

        private string NameAtIndex(int index)
            => index >= 0 && index < _history.Count ? _history[index].Name : string.Empty;

        /// <summary>
        /// Starts a Switch-policy undo/redo operation: cancels any previous one, then runs the given task.
        /// </summary>
        private UniTask<ExecutionResult> StartUndoRedoTask(Func<CancellationToken, UniTask> operation)
        {
            var prevTcs = _undoRedoTcs;
            ReplaceCts();
            prevTcs?.TrySetResult(ExecutionResult.Cancelled);

            var tcs = new UniTaskCompletionSource<ExecutionResult>();
            _undoRedoTcs = tcs;
            RunUndoRedoAsync(operation, tcs).Forget();
            return tcs.Task;
        }

        private async UniTask RunUndoRedoAsync(Func<CancellationToken, UniTask> operation, UniTaskCompletionSource<ExecutionResult> tcs)
        {
            try
            {
                await operation(CtsToken);
                tcs.TrySetResult(ExecutionResult.Completed);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetResult(ExecutionResult.Cancelled);
            }
            finally
            {
                if (_undoRedoTcs == tcs)
                    _undoRedoTcs = null;
            }
        }
    }
}
