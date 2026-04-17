namespace UniTaskCommandBus
{
    /// <summary>
    /// The outcome of a command execution attempt.
    /// </summary>
    public enum ExecutionResult
    {
        /// <summary>The command ran and finished normally.</summary>
        Completed,

        /// <summary>The command was rejected by the async policy before execution started.</summary>
        Dropped,

        /// <summary>The command was cancelled during or before execution.</summary>
        Cancelled,

        /// <summary>The command's lambda threw an unhandled exception.</summary>
        Faulted,
    }
}
