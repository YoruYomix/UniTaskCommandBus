namespace UniTaskCommandBus
{
    /// <summary>
    /// Indicates the execution context passed to the command's execute delegate.
    /// </summary>
    public enum ExecutionPhase
    {
        /// <summary>Execution via <c>Invoker&lt;T&gt;</c> — no history context.</summary>
        None,

        /// <summary>First-time execution via <c>HistoryInvoker&lt;T&gt;</c>.</summary>
        Execute,

        /// <summary>Re-execution via <c>HistoryInvoker&lt;T&gt;.Redo()</c> or forward step of <c>JumpTo</c>.</summary>
        Redo,
    }
}
