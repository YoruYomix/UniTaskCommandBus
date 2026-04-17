namespace UniTaskCommandBus
{
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
