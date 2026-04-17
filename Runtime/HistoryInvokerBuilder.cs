namespace UniTaskCommandBus
{
    /// <summary>
    /// Builder for creating a <see cref="HistoryInvoker{T}"/>.
    /// Obtained from <see cref="InvokerBuilder{T}.WithHistory"/>.
    /// </summary>
    public class HistoryInvokerBuilder<T>
    {
        private AsyncPolicy _policy;
        private readonly int _maxSize;

        internal HistoryInvokerBuilder(AsyncPolicy policy, int maxSize)
        {
            _policy = policy;
            _maxSize = maxSize;
        }

        /// <summary>Sets the async policy for the history invoker.</summary>
        public HistoryInvokerBuilder<T> WithPolicy(AsyncPolicy policy)
        {
            _policy = policy;
            return this;
        }

        /// <summary>Builds and returns the configured <see cref="HistoryInvoker{T}"/>.</summary>
        public HistoryInvoker<T> Build() => new HistoryInvoker<T>(_policy, _maxSize);
    }
}
