using System;

namespace UniTaskCommandBus
{
    /// <summary>
    /// A synchronous lambda command. Wraps execute and undo delegates.
    /// </summary>
    public class Command<T>
    {
        private readonly Action<T, ExecutionPhase> _execute;
        private readonly Action<T> _undo;

        /// <summary>The display name of this command, used in history UI.</summary>
        public string Name { get; }

        /// <summary>
        /// Creates a command from a simple execute delegate (no phase awareness).
        /// </summary>
        public Command(Action<T> execute, Action<T> undo = null, string name = "")
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            _execute = (payload, _) => execute(payload);
            _undo = undo ?? (_ => { });
            Name = name ?? string.Empty;
        }

        /// <summary>
        /// Creates a command from a phase-aware execute delegate.
        /// </summary>
        public Command(Action<T, ExecutionPhase> execute, Action<T> undo = null, string name = "")
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _undo = undo ?? (_ => { });
            Name = name ?? string.Empty;
        }

        internal void InvokeExecute(T payload, ExecutionPhase phase) => _execute(payload, phase);
        internal void InvokeUndo(T payload) => _undo(payload);
    }
}
