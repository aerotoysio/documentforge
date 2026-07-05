namespace DocumentForge.Studio.Core.Settings;

public sealed class StudioSettings
{
    public string DefaultDataDirectory { get; set; } = @"C:\data\documentForge";

    /// <summary>Guard added to unbounded SELECTs in the query workbench.</summary>
    public int DefaultQueryLimit { get; set; } = 1000;

    /// <summary>Auto-cancel a query after this many seconds (0 = no timeout).</summary>
    public int DefaultQueryTimeoutSeconds { get; set; } = 30;

    /// <summary>Reconnect saved connections automatically on startup so the
    /// Object Explorer isn't empty after a relaunch.</summary>
    public bool ReconnectOnStartup { get; set; } = true;
}
