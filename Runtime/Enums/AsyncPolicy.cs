namespace UniTaskCommandBus
{
    /// <summary>
    /// Defines how the Invoker handles a new command when another is already running.
    /// </summary>
    public enum AsyncPolicy
    {
        /// <summary>Discard the new command if one is already running.</summary>
        Drop,

        /// <summary>Queue the new command and execute it after the current one finishes.</summary>
        Sequential,

        /// <summary>Cancel the current command and start the new one immediately.</summary>
        Switch,

        /// <summary>
        /// While running, keep only the last incoming command. When done, run that last one.
        /// Intermediate commands are dropped.
        /// </summary>
        ThrottleLast,

        /// <summary>Run all commands concurrently without restriction.</summary>
        Parallel,
    }
}
