namespace XPlatUI;

/// <summary>
/// What a drag source allows a drop target to do, and what the target decided to do with the data.
/// </summary>
[Flags]
public enum XPlatDragDropEffects
{
    /// <summary>Nothing: the target refuses the data and the platform shows a "no drop" cursor.</summary>
    None = 0,

    /// <summary>The target takes a copy; the source keeps its original.</summary>
    Copy = 1,

    /// <summary>The target takes the data away, so the source may delete its original.</summary>
    Move = 2,

    /// <summary>The target keeps a reference to the data instead of a copy of it.</summary>
    Link = 4,
}
