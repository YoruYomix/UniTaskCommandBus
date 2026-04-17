using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace UniTaskCommandBus
{
    /// <summary>
    /// An asynchronous lambda command. Wraps async execute and undo delegates.
    /// </summary>
    public class AsyncCommand<T>
    {
        private readonly Func<T, ExecutionPhase, CancellationToken, UniTask> _execute;
        private readonly Func<T, CancellationToken, UniTask> _undo;

        /// <summary>The display name of this command, used in history UI.</summary>
        public string Name { get; }

        /// <summary>
        /// Creates an async command from a simple execute delegate (no phase awareness).
        /// </summary>
        public AsyncCommand(
            Func<T, CancellationToken, UniTask> execute,
            Func<T, CancellationToken, UniTask> undo = null,
            string name = "")
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            _execute = (payload, _, ct) => execute(payload, ct);
            _undo = undo ?? ((_, __) => UniTask.CompletedTask);
            Name = name ?? string.Empty;
        }

        /// <summary>
        /// Creates an async command from a phase-aware execute delegate.
        /// </summary>
        public AsyncCommand(
            Func<T, ExecutionPhase, CancellationToken, UniTask> execute,
            Func<T, CancellationToken, UniTask> undo = null,
            string name = "")
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _undo = undo ?? ((_, __) => UniTask.CompletedTask);
            Name = name ?? string.Empty;
        }

        internal UniTask InvokeExecute(T payload, ExecutionPhase phase, CancellationToken ct)
            => _execute(payload, phase, ct);

        internal UniTask InvokeUndo(T payload, CancellationToken ct)
            => _undo(payload, ct);
    }
}
