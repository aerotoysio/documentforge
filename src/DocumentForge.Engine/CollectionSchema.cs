namespace DocumentForge.Engine;

/// <summary>
/// Issue #106 — the type a field is constrained to. <see cref="Number"/>
/// accepts any numeric (int or double); <see cref="Int"/> only integrals.
/// </summary>
public enum FieldTypeConstraint { String, Int, Number, Bool, DateTime, Object, Array }

/// <summary>
/// Issue #151 — what happens to a referencing document when the document it
/// points at is deleted.
/// </summary>
public enum OnDeleteAction
{
    /// <summary>Refuse the delete while any referencing document exists. Default.</summary>
    Restrict,
    /// <summary>Null out the referencing field (subject to the child's own schema —
    /// a ref field that is also <c>required</c> cannot be nulled, so that
    /// combination behaves like Restrict).</summary>
    SetNull,
    /// <summary>Delete referencing documents too, recursively (cycle-safe).</summary>
    Cascade,
}

/// <summary>
/// Issue #151 — a referential-integrity constraint: <see cref="Field"/> on a
/// document in this collection must equal <see cref="TargetField"/> of some
/// document in <see cref="Collection"/>. Absent/null fields are exempt
/// (use <c>required</c> to forbid that). <see cref="TargetField"/> of
/// <c>_id</c> resolves via the primary key; any other target field should
/// carry a unique index or lookups degrade to a scan.
/// </summary>
public sealed record RefConstraint(
    string Field,
    string Collection,
    string TargetField,
    OnDeleteAction OnDelete);

/// <summary>
/// Issue #106 — an opt-in per-collection integrity contract. All parts
/// are optional and additive:
/// <list type="bullet">
///   <item><see cref="Required"/> — fields that must be present and non-null.</item>
///   <item><see cref="Types"/> — a present field must be of the declared type.</item>
///   <item><see cref="Checks"/> — CHECK constraints, reusing the #103 condition
///   model (e.g. <c>seatCount &gt;= 0</c>); every one must hold.</item>
///   <item><see cref="Refs"/> — referential-integrity constraints (issue #151):
///   the field must point at an existing document in another collection, and
///   deletes of referenced documents honour each ref's <see cref="OnDeleteAction"/>.</item>
/// </list>
/// A collection with no schema is validated against nothing — the schemaless
/// story is preserved.
/// </summary>
public sealed record CollectionSchema(
    string Collection,
    IReadOnlyList<string> Required,
    IReadOnlyDictionary<string, FieldTypeConstraint> Types,
    IReadOnlyList<UpdateCondition> Checks,
    IReadOnlyList<RefConstraint>? Refs = null)
{
    /// <summary>Never-null view of <see cref="Refs"/>.</summary>
    public IReadOnlyList<RefConstraint> RefsOrEmpty => Refs ?? Array.Empty<RefConstraint>();
}
