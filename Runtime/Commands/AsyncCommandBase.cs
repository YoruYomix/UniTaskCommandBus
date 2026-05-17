using System.Threading;
using Cysharp.Threading.Tasks;

namespace UniTaskCommandBus
{
    /// <summary>
    /// Base class for payload-free asynchronous commands.
    /// Internally this is an <see cref="AsyncCommandBase{T}"/> using <see cref="CommandUnit"/>.
    /// </summary>
    public abstract class AsyncCommandBase : AsyncCommandBase<CommandUnit>
    {
        /// <summary>Executes the command asynchronously without a payload.</summary>
        public abstract UniTask ExecuteAsync(CancellationToken ct);

        /// <summary>
        /// Executes the command asynchronously with phase context.
        /// Defaults to calling <see cref="ExecuteAsync(CancellationToken)"/>.
        /// </summary>
        public virtual UniTask ExecuteAsync(ExecutionPhase phase, CancellationToken ct)
            => ExecuteAsync(ct);

        /// <inheritdoc />
        public sealed override UniTask ExecuteAsync(CommandUnit payload, CancellationToken ct)
            => ExecuteAsync(ct);

        /// <inheritdoc />
        public sealed override UniTask ExecuteAsync(CommandUnit payload, ExecutionPhase phase, CancellationToken ct)
            => ExecuteAsync(phase, ct);

        /// <summary>Undoes the command asynchronously. Default implementation completes immediately.</summary>
        public virtual UniTask UndoAsync(CancellationToken ct) => UniTask.CompletedTask;

        /// <inheritdoc />
        public sealed override UniTask UndoAsync(CommandUnit payload, CancellationToken ct)
            => UndoAsync(ct);
    }

    /// <summary>
    /// Base class for asynchronous class-based commands.
    /// Override <see cref="ExecuteAsync(T, CancellationToken)"/> at minimum;
    /// override <see cref="UndoAsync"/> if Undo support is needed.
    /// </summary>
    public abstract class AsyncCommandBase<T>
    {
        /// <summary>Executes the command asynchronously.</summary>
        public abstract UniTask ExecuteAsync(T payload, CancellationToken ct);

        /// <summary>
        /// Executes the command asynchronously with phase context.
        /// Defaults to calling <see cref="ExecuteAsync(T, CancellationToken)"/>.
        /// </summary>
        public virtual UniTask ExecuteAsync(T payload, ExecutionPhase phase, CancellationToken ct)
            => ExecuteAsync(payload, ct);

        /// <summary>Undoes the command asynchronously. Default implementation completes immediately.</summary>
        public virtual UniTask UndoAsync(T payload, CancellationToken ct) => UniTask.CompletedTask;

        /// <summary>Display name used in history UI. Defaults to empty string.</summary>
        public virtual string Name => string.Empty;
    }
}
