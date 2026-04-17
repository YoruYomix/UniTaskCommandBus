namespace UniTaskCommandBus
{
    /// <summary>
    /// Static factory for creating Invoker instances.
    /// Always use this as the entry point rather than constructing invokers directly.
    /// </summary>
    public static class CommandBus
    {
        /// <summary>
        /// Creates a new builder for an invoker with the given payload type <typeparamref name="T"/>.
        /// Chain <c>.WithPolicy(...)</c>, optionally <c>.WithHistory(...)</c>, then <c>.Build()</c>.
        /// </summary>
        public static InvokerBuilder<T> Create<T>() => new InvokerBuilder<T>();
    }
}
