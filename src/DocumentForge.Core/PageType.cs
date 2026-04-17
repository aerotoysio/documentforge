namespace DocumentForge.Core;

public enum PageType : byte
{
    Header = 0,
    Data = 1,
    BTreeInternal = 2,
    BTreeLeaf = 3,
    Overflow = 4,
    FreeList = 5,
    CollectionCatalog = 6
}
