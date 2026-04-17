using System;

namespace UniTaskCommandBus
{
    /// <summary>
    /// Builder for creating an <see cref="Invoker{T}"/> (without history).
    /// Obtained from <see cref="CommandBus.Create{T}"/>.
    /// </summary>
    public class InvokerBuilder<T>
    {
        private AsyncPolicy _policy = AsyncPolicy.Sequential;

        internal InvokerBuilder() { }

        /// <summary>Sets the async policy for the invoker. Default is <see cref="AsyncPolicy.Sequential"/>.</summary>
        public InvokerBuilder<T> WithPolicy(AsyncPolicy policy)
        {
            _policy = policy;
            return this;
        }

        /// <summary>
        /// Switches to a history-enabled builder.
        /// </summary>
        /// <param name="maxSize">Maximum number of history entries. Must be ≥ 1.</param>
        public HistoryInvokerBuilder<T> WithHistory(int maxSize = 20)
        {
            if (maxSize < 1)
                throw new ArgumentOutOfRangeException(nameof(maxSize), "최소 1 이상의 값을 지정해야 합니다");
            return new HistoryInvokerBuilder<T>(_policy, maxSize);
        }

        /// <summary>Builds and returns the configured <see cref="Invoker{T}"/>.</summary>
        public Invoker<T> Build() => new Invoker<T>(_policy);
    }
}
