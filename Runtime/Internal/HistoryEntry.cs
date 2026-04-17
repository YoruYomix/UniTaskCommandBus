namespace UniTaskCommandBus
{
    /// <summary>
    /// Stores a single history record: the command adapter, its payload, and the name snapshot
    /// captured at the moment of execution.
    /// </summary>
    internal sealed class HistoryEntry<T>
    {
        public ICommand<T> Command { get; }
        public T Payload { get; }
        public string Name { get; }

        public HistoryEntry(ICommand<T> command, T payload, string name)
        {
            Command = command;
            Payload = payload;
            Name = name;
        }
    }
}
