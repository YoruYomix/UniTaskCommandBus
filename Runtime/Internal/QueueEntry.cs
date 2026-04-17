using Cysharp.Threading.Tasks;

namespace UniTaskCommandBus
{
    /// <summary>
    /// Holds a pending command entry for Sequential and ThrottleLast policies.
    /// Used by the execution engine introduced in Stage 2.
    /// </summary>
    internal sealed class QueueEntry<T>
    {
        public ICommand<T> Command { get; }
        public T Payload { get; }
        public ExecutionPhase Phase { get; }
        public UniTaskCompletionSource<ExecutionResult> CompletionSource { get; }

        public QueueEntry(ICommand<T> command, T payload, ExecutionPhase phase)
        {
            Command = command;
            Payload = payload;
            Phase = phase;
            CompletionSource = new UniTaskCompletionSource<ExecutionResult>();
        }
    }
}
