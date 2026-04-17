namespace UniTaskCommandBus
{
    /// <summary>
    /// Describes the type of history change that triggered <c>OnHistoryChanged</c>.
    /// </summary>
    public enum HistoryActionType
    {
        /// <summary>A new command was added to the history.</summary>
        Execute,

        /// <summary>The pointer moved left (undo).</summary>
        Undo,

        /// <summary>The pointer moved right (redo).</summary>
        Redo,

        /// <summary>The pointer jumped to an arbitrary position via <c>JumpTo</c>.</summary>
        Jump,

        /// <summary>The last history entry was removed via <c>Pop</c>.</summary>
        Pop,

        /// <summary>The entire history was cleared via <c>Clear</c>.</summary>
        Clear,
    }
}
