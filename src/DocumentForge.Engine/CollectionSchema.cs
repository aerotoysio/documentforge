namespace DocumentForge.Engine;

/// <summary>
/// Issue #106 — the type a field is constrained to. <see cref="Number"/>
/// accepts any numeric (int or double); <see cref="Int"/> only integrals.
/// </summary>
public enum FieldTypeConstraint { String, Int, Number, Bool, DateTime, Object, Array }

/// <summary>
/// Issue #106 — an opt-in per-collection integrity contract. All three parts
/// are optional and additive:
/// <list type="bullet">
///   <item><see cref="Required"/> — fields that must be present and non-null.</item>
///   <item><see cref="Types"/> — a present field must be of the declared type.</item>
///   <item><see cref="Checks"/> — CHECK constraints, reusing the #103 condition
///   model (e.g. <c>seatCount &gt;= 0</c>); every one must hold.</item>
/// </list>
/// A collection with no schema is validated against nothing — the schemaless
/// story is preserved.
/// </summary>
public sealed record CollectionSchema(
    string Collection,
    IReadOnlyList<string> Required,
    IReadOnlyDictionary<string, FieldTypeConstraint> Types,
    IReadOnlyList<UpdateCondition> Checks);
