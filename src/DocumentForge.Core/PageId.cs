namespace DocumentForge.Core;

public readonly struct PageId : IEquatable<PageId>, IComparable<PageId>
{
    public static readonly PageId Invalid = new(Constants.InvalidPageId);
    public static readonly PageId Header = new(0);
    public static readonly PageId CollectionCatalog = new(1);

    public uint Value { get; }

    public PageId(uint value) => Value = value;

    public bool IsValid => Value != Constants.InvalidPageId;
    public long FileOffset => (long)Value * Constants.PageSize;

    public bool Equals(PageId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is PageId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public int CompareTo(PageId other) => Value.CompareTo(other.Value);
    public override string ToString() => $"Page({Value})";

    public static bool operator ==(PageId left, PageId right) => left.Equals(right);
    public static bool operator !=(PageId left, PageId right) => !left.Equals(right);
    public static PageId operator +(PageId left, uint offset) => new(left.Value + offset);
    public static implicit operator uint(PageId id) => id.Value;
    public static explicit operator PageId(uint value) => new(value);
}
