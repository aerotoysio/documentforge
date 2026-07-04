namespace DocumentForge.Studio.Core.Settings;

public sealed class StudioSettings
{
    public string DefaultDataDirectory { get; set; } = @"C:\data\documentForge";

    /// <summary>Guard added to unbounded SELECTs in the query workbench.</summary>
    public int DefaultQueryLimit { get; set; } = 1000;
}
