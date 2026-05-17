using Cysharp.Threading.Tasks;
using System.Threading;

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
        public CancellationToken CancellationToken { get; }
        public UniTaskCompletionSource<ExecutionResult> CompletionSource { get; }

        public QueueEntry(ICommand<T> command, T payload, ExecutionPhase phase, CancellationToken cancellationToken = default)
        {
            Command = command;
            Payload = payload;
            Phase = phase;
            CancellationToken = cancellationToken;
            CompletionSource = new UniTaskCompletionSource<ExecutionResult>();
        }
    }
}
