namespace UniTaskCommandBus
{
    /// <summary>
    /// Base class for payload-free synchronous commands.
    /// Internally this is a <see cref="CommandBase{T}"/> using <see cref="CommandUnit"/>.
    /// </summary>
    public abstract class CommandBase : CommandBase<CommandUnit>
    {
        /// <summary>Executes the command without a payload.</summary>
        public abstract void Execute();

        /// <summary>Executes the command with phase context. Defaults to calling <see cref="Execute()"/>.</summary>
        public virtual void Execute(ExecutionPhase phase) => Execute();

        /// <inheritdoc />
        public sealed override void Execute(CommandUnit payload) => Execute();

        /// <inheritdoc />
        public sealed override void Execute(CommandUnit payload, ExecutionPhase phase) => Execute(phase);

        /// <summary>Undoes the command. Default implementation does nothing.</summary>
        public virtual void Undo() { }

        /// <inheritdoc />
        public sealed override void Undo(CommandUnit payload) => Undo();
    }

    /// <summary>
    /// Base class for synchronous class-based commands.
    /// Override <see cref="Execute(T)"/> at minimum; override <see cref="Undo"/> if Undo support is needed.
    /// </summary>
    public abstract class CommandBase<T>
    {
        /// <summary>Executes the command with the given payload.</summary>
        public abstract void Execute(T payload);

        /// <summary>
        /// Executes the command with phase context. Defaults to calling <see cref="Execute(T)"/>.
        /// Override to branch on <see cref="ExecutionPhase"/>.
        /// </summary>
        public virtual void Execute(T payload, ExecutionPhase phase) => Execute(payload);

        /// <summary>Undoes the command. Default implementation does nothing.</summary>
        public virtual void Undo(T payload) { }

        /// <summary>Display name used in history UI. Defaults to empty string.</summary>
        public virtual string Name => string.Empty;
    }
}
