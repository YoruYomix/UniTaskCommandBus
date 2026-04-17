using System.Threading;
using Cysharp.Threading.Tasks;

namespace UniTaskCommandBus
{
    /// <summary>
    /// Internal unified command interface. Adapts both sync and async commands
    /// into a single async interface for the execution engine.
    /// </summary>
    internal interface ICommand<T>
    {
        string GetName();
        UniTask InvokeExecute(T payload, ExecutionPhase phase, CancellationToken ct);
        UniTask InvokeUndo(T payload, CancellationToken ct);
    }

    internal sealed class SyncCommandAdapter<T> : ICommand<T>
    {
        private readonly Command<T> _cmd;
        public SyncCommandAdapter(Command<T> cmd) => _cmd = cmd;
        public string GetName() => _cmd.Name;

        public UniTask InvokeExecute(T payload, ExecutionPhase phase, CancellationToken ct)
        {
            _cmd.InvokeExecute(payload, phase);
            return UniTask.CompletedTask;
        }

        public UniTask InvokeUndo(T payload, CancellationToken ct)
        {
            _cmd.InvokeUndo(payload);
            return UniTask.CompletedTask;
        }
    }

    internal sealed class SyncClassCommandAdapter<T> : ICommand<T>
    {
        private readonly CommandBase<T> _cmd;
        public SyncClassCommandAdapter(CommandBase<T> cmd) => _cmd = cmd;
        public string GetName() => _cmd.Name;

        public UniTask InvokeExecute(T payload, ExecutionPhase phase, CancellationToken ct)
        {
            _cmd.Execute(payload, phase);
            return UniTask.CompletedTask;
        }

        public UniTask InvokeUndo(T payload, CancellationToken ct)
        {
            _cmd.Undo(payload);
            return UniTask.CompletedTask;
        }
    }

    internal sealed class AsyncCommandAdapter<T> : ICommand<T>
    {
        private readonly AsyncCommand<T> _cmd;
        public AsyncCommandAdapter(AsyncCommand<T> cmd) => _cmd = cmd;
        public string GetName() => _cmd.Name;

        public UniTask InvokeExecute(T payload, ExecutionPhase phase, CancellationToken ct)
            => _cmd.InvokeExecute(payload, phase, ct);

        public UniTask InvokeUndo(T payload, CancellationToken ct)
            => _cmd.InvokeUndo(payload, ct);
    }

    internal sealed class AsyncClassCommandAdapter<T> : ICommand<T>
    {
        private readonly AsyncCommandBase<T> _cmd;
        public AsyncClassCommandAdapter(AsyncCommandBase<T> cmd) => _cmd = cmd;
        public string GetName() => _cmd.Name;

        public UniTask InvokeExecute(T payload, ExecutionPhase phase, CancellationToken ct)
            => _cmd.ExecuteAsync(payload, phase, ct);

        public UniTask InvokeUndo(T payload, CancellationToken ct)
            => _cmd.UndoAsync(payload, ct);
    }
}
