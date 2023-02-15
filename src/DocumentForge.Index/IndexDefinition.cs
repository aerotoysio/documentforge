using DocumentForge.Core;

namespace DocumentForge.Index;

public sealed class IndexDefinition
{
    public string Name { get; init; } = "";
    public CollectionName CollectionName { get; init; }
    public string JsonPath { get; init; } = "";
    public bool IsUnique { get; init; }
    // First page of the index's persistent entry storage
    public PageId RootPage { get; set; } = PageId.Invalid;
}
