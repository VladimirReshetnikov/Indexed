namespace Indexed.Targets;

/// <summary>
/// Controls how an Indexed daemon keeps its target index current after the
/// initial scan.
/// </summary>
public enum IndexUpdateMode
{
    /// <summary>
    /// Watch filesystem/revision changes and periodically reconcile in the
    /// background. This is the default mode.
    /// </summary>
    Live = 0,

    /// <summary>
    /// Do not start automatic change sources. The index changes only during
    /// the initial scan and explicit rescan requests.
    /// </summary>
    Manual = 1,
}
