using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace UniTaskCommandBus
{
    /// <summary>
    /// Command invoker without history. Supports sync and async execution with all five AsyncPolicy variants.
    /// Create via <see cref="CommandBus.Create{T}"/> — do not instantiate directly.
    /// </summary>
    public class Invoker<T> : IDisposable
    {
        protected readonly AsyncPolicy _policy;
        private bool _disposed;

        // Execution state
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _isRunning;       // Drop / ThrottleLast: a command is currently executing
        private bool _isLoopRunning;   // Sequential: the dequeue loop is active
        private readonly Queue<QueueEntry<T>> _queue = new Queue<QueueEntry<T>>();
        private QueueEntry<T> _throttleSlot;

        /// <summary>
        /// Fired when a command lambda throws an unhandled exception.
        /// The invoker continues operating normally after the event fires.
        /// </summary>
        public event Action<Exception> OnError;

        internal Invoker(AsyncPolicy policy)
        {
            _policy = policy;
        }

        // ── Dispose guard ────────────────────────────────────────────────────

        /// <summary>Throws <see cref="ObjectDisposedException"/> if this invoker has been disposed.</summary>
        protected void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);
        }

        // ── Extension hooks for HistoryInvoker ───────────────────────────────

        /// <summary>
        /// Returns the execution phase to pass to commands. Overridden by HistoryInvoker to return Execute.
        /// </summary>
        protected virtual ExecutionPhase GetExecutionPhase() => ExecutionPhase.None;

        /// <summary>
        /// Called just before a command's lambda runs (after policy gate).
        /// HistoryInvoker overrides this to record history and fire OnHistoryChanged.
        /// The three steps (record → event → lambda) must stay in this order.
        /// </summary>
        private protected virtual void OnBeforeExecute(ICommand<T> cmd, T payload, string name) { }

        // ── Sync Execute overloads ────────────────────────────────────────────

        /// <summary>Executes a command according to the configured policy (fire-and-forget).</summary>
        public ExecutionResult Execute(Command<T> cmd, T payload)
        {
            ThrowIfDisposed();
            return ExecuteFireAndForget(new SyncCommandAdapter<T>(cmd), payload, GetExecutionPhase());
        }

        /// <summary>Executes a synchronous lambda command with default payload.</summary>
        public ExecutionResult Execute(Command<T> cmd)
            => Execute(cmd, default);

        /// <summary>Executes a class-based synchronous command according to the configured policy.</summary>
        public ExecutionResult Execute(CommandBase<T> cmd, T payload)
        {
            ThrowIfDisposed();
            return ExecuteFireAndForget(new SyncClassCommandAdapter<T>(cmd), payload, GetExecutionPhase());
        }

        /// <summary>
        /// Shortcut: pass execute/undo delegates directly without creating a <see cref="Command{T}"/>.
        /// Primarily for use with <see cref="CommandUnit"/> (payload-free) scenarios.
        /// </summary>
        public ExecutionResult Execute(Action<T> execute, Action<T> undo = null, string name = "")
            => Execute(new Command<T>(execute, undo, name), default);

        /// <summary>Starts an async lambda command according to the configured policy (fire-and-forget).</summary>
        public ExecutionResult Execute(AsyncCommand<T> cmd, T payload)
        {
            ThrowIfDisposed();
            return ExecuteFireAndForget(new AsyncCommandAdapter<T>(cmd), payload, GetExecutionPhase());
        }

        /// <summary>Starts an async class-based command according to the configured policy (fire-and-forget).</summary>
        public ExecutionResult Execute(AsyncCommandBase<T> cmd, T payload)
        {
            ThrowIfDisposed();
            return ExecuteFireAndForget(new AsyncClassCommandAdapter<T>(cmd), payload, GetExecutionPhase());
        }

        // ── Async Execute overloads ──────────────────────────────────────────

        /// <summary>Awaits completion of a sync lambda command, respecting the configured policy.</summary>
        public UniTask<ExecutionResult> ExecuteAsync(Command<T> cmd, T payload)
        {
            ThrowIfDisposed();
            return SubmitAsync(new SyncCommandAdapter<T>(cmd), payload, GetExecutionPhase());
        }

        /// <summary>Awaits completion of a sync lambda command with default payload.</summary>
        public UniTask<ExecutionResult> ExecuteAsync(Command<T> cmd)
            => ExecuteAsync(cmd, default);

        /// <summary>Awaits completion of a class-based sync command, respecting the configured policy.</summary>
        public UniTask<ExecutionResult> ExecuteAsync(CommandBase<T> cmd, T payload)
        {
            ThrowIfDisposed();
            return SubmitAsync(new SyncClassCommandAdapter<T>(cmd), payload, GetExecutionPhase());
        }

        /// <summary>Awaits completion of an async lambda command, respecting the configured policy.</summary>
        public UniTask<ExecutionResult> ExecuteAsync(AsyncCommand<T> cmd, T payload)
        {
            ThrowIfDisposed();
            return SubmitAsync(new AsyncCommandAdapter<T>(cmd), payload, GetExecutionPhase());
        }

        /// <summary>Awaits completion of an async lambda command with default payload.</summary>
        public UniTask<ExecutionResult> ExecuteAsync(AsyncCommand<T> cmd)
            => ExecuteAsync(cmd, default);

        /// <summary>Awaits completion of an async class-based command, respecting the configured policy.</summary>
        public UniTask<ExecutionResult> ExecuteAsync(AsyncCommandBase<T> cmd, T payload)
        {
            ThrowIfDisposed();
            return SubmitAsync(new AsyncClassCommandAdapter<T>(cmd), payload, GetExecutionPhase());
        }

        // ── Cancel / CancelAll ───────────────────────────────────────────────

        /// <summary>
        /// Cancels the currently running command. Queued commands are preserved and will run afterward.
        /// </summary>
        public void Cancel()
        {
            ThrowIfDisposed();
            ReplaceCts();
        }

        /// <summary>
        /// Cancels the running command and resolves all queued/slotted commands as <see cref="ExecutionResult.Cancelled"/>.
        /// </summary>
        public void CancelAll()
        {
            ThrowIfDisposed();
            ReplaceCts();

            while (_queue.Count > 0)
                _queue.Dequeue().CompletionSource.TrySetResult(ExecutionResult.Cancelled);

            if (_throttleSlot != null)
            {
                _throttleSlot.CompletionSource.TrySetResult(ExecutionResult.Cancelled);
                _throttleSlot = null;
            }
        }

        // ── Dispose ──────────────────────────────────────────────────────────

        /// <summary>
        /// Disposes this invoker, cancelling all in-flight and queued work.
        /// After disposal any call throws <see cref="ObjectDisposedException"/>.
        /// </summary>
        public virtual void Dispose()
        {
            if (_disposed) return;
            CancelAll();
            _disposed = true;
            _cts.Dispose();
            OnError = null;
        }

        // ── Internal execution engine ─────────────────────────────────────────

        private protected CancellationToken CtsToken => _cts.Token;

        private protected void ReplaceCts()
        {
            var old = _cts;
            _cts = new CancellationTokenSource();
            old.Cancel();
            old.Dispose();
        }

        private ExecutionResult ExecuteFireAndForget(ICommand<T> cmd, T payload, ExecutionPhase phase)
        {
            var entry = new QueueEntry<T>(cmd, payload, phase);
            var result = SubmitEntry(entry);
            entry.CompletionSource.Task.Forget();
            return result;
        }

        private UniTask<ExecutionResult> SubmitAsync(ICommand<T> cmd, T payload, ExecutionPhase phase)
        {
            var entry = new QueueEntry<T>(cmd, payload, phase);
            SubmitEntry(entry);
            return entry.CompletionSource.Task;
        }

        private ExecutionResult SubmitEntry(QueueEntry<T> entry)
        {
            switch (_policy)
            {
                case AsyncPolicy.Drop:
                    if (_isRunning)
                    {
                        entry.CompletionSource.TrySetResult(ExecutionResult.Dropped);
                        return ExecutionResult.Dropped;
                    }
                    _isRunning = true;
                    RunDropAsync(entry).Forget();
                    return ExecutionResult.Completed;

                case AsyncPolicy.Sequential:
                    if (_isLoopRunning)
                    {
                        _queue.Enqueue(entry);
                        return ExecutionResult.Completed;
                    }
                    _isLoopRunning = true;
                    RunSequentialLoopAsync(entry).Forget();
                    return ExecutionResult.Completed;

                case AsyncPolicy.Switch:
                    ReplaceCts();
                    RunSwitchAsync(entry).Forget();
                    return ExecutionResult.Completed;

                case AsyncPolicy.ThrottleLast:
                    if (_isRunning)
                    {
                        _throttleSlot?.CompletionSource.TrySetResult(ExecutionResult.Dropped);
                        _throttleSlot = entry;
                        return ExecutionResult.Completed;
                    }
                    _isRunning = true;
                    RunThrottleLastLoopAsync(entry).Forget();
                    return ExecutionResult.Completed;

                case AsyncPolicy.Parallel:
                    RunParallelAsync(entry).Forget();
                    return ExecutionResult.Completed;

                default:
                    entry.CompletionSource.TrySetResult(ExecutionResult.Completed);
                    return ExecutionResult.Completed;
            }
        }

        private async UniTask RunDropAsync(QueueEntry<T> entry)
        {
            var result = await RunLambdaCore(entry);
            _isRunning = false;                          // reset before TCS so continuations see correct state
            entry.CompletionSource.TrySetResult(result);
        }

        private async UniTask RunSequentialLoopAsync(QueueEntry<T> firstEntry)
        {
            try
            {
                var r = await RunLambdaCore(firstEntry);
                firstEntry.CompletionSource.TrySetResult(r);
                while (_queue.Count > 0)
                {
                    var next = _queue.Dequeue();
                    var rn = await RunLambdaCore(next);
                    next.CompletionSource.TrySetResult(rn);
                }
            }
            finally { _isLoopRunning = false; }
        }

        private async UniTask RunSwitchAsync(QueueEntry<T> entry)
        {
            var result = await RunLambdaCore(entry);
            entry.CompletionSource.TrySetResult(result);
        }

        private async UniTask RunThrottleLastLoopAsync(QueueEntry<T> firstEntry)
        {
            try
            {
                var r = await RunLambdaCore(firstEntry);
                firstEntry.CompletionSource.TrySetResult(r);
                while (_throttleSlot != null)
                {
                    var next = _throttleSlot;
                    _throttleSlot = null;
                    var rn = await RunLambdaCore(next);
                    next.CompletionSource.TrySetResult(rn);
                }
            }
            finally { _isRunning = false; }
        }

        private async UniTask RunParallelAsync(QueueEntry<T> entry)
        {
            var result = await RunLambdaCore(entry);
            entry.CompletionSource.TrySetResult(result);
        }

        /// <summary>
        /// Fires OnBeforeExecute hook and runs the command lambda.
        /// Catches all exceptions and returns Completed / Cancelled / Faulted — never throws.
        /// TCS resolution is handled by each caller so flags can be reset first.
        /// </summary>
        private protected async UniTask<ExecutionResult> RunLambdaCore(QueueEntry<T> entry)
        {
            OnBeforeExecute(entry.Command, entry.Payload, entry.Command.GetName());
            try
            {
                await entry.Command.InvokeExecute(entry.Payload, entry.Phase, _cts.Token);
                return ExecutionResult.Completed;
            }
            catch (OperationCanceledException)
            {
                return ExecutionResult.Cancelled;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
                return ExecutionResult.Faulted;
            }
        }
    }
}
