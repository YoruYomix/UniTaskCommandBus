namespace UniTaskCommandBus
{
    /// <summary>
    /// Empty payload type for commands that require no arguments.
    /// Use as the type parameter T when no data needs to be passed.
    /// </summary>
    public readonly struct CommandUnit
    {
        /// <summary>The default (and only) value of <see cref="CommandUnit"/>.</summary>
        public static readonly CommandUnit Default = default;
    }
}
