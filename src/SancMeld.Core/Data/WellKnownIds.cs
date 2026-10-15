namespace SancMeld.Core.Data;

/// <summary>
/// Identifiers for the rows the schema itself ships.
///
/// They are fixed constants rather than generated values because they are referenced by
/// migrations: a seeded row whose key changed between two runs would be inserted twice
/// on some databases and orphan every designation hanging off the old one on others.
/// </summary>
public static class WellKnownIds
{
    public static readonly Guid EuJurisdiction = new("0195a1f0-0000-7000-8000-000000000001");
    public static readonly Guid EuFsfSource = new("0195a1f0-0000-7000-8000-000000000101");

    public static readonly Guid UnJurisdiction = new("0195a1f0-0000-7000-8000-000000000002");
    public static readonly Guid UnScSource = new("0195a1f0-0000-7000-8000-000000000102");

    public static readonly Guid FrJurisdiction = new("0195a1f0-0000-7000-8000-000000000003");
    public static readonly Guid FrGelsSource = new("0195a1f0-0000-7000-8000-000000000103");

    public static readonly Guid PlJurisdiction = new("0195a1f0-0000-7000-8000-000000000004");
    public static readonly Guid PlMswiaSource = new("0195a1f0-0000-7000-8000-000000000104");

    public static readonly Guid CzJurisdiction = new("0195a1f0-0000-7000-8000-000000000005");
    public static readonly Guid CzMzvSource = new("0195a1f0-0000-7000-8000-000000000105");

    /// <summary>The rules-based matcher. A trained model becomes a second row, not an edit.</summary>
    public static readonly Guid RulesAlgorithm = new("0195a1f0-0000-7000-8000-000000000201");

    /// <summary>bge-m3 through Ollama, the model the retrieval measurement was made with.</summary>
    public static readonly Guid BgeM3Model = new("0195a1f0-0000-7000-8000-000000000301");

    /// <summary>
    /// Seeded rows need a fixed timestamp. Anything derived from the clock would make
    /// every migration regeneration produce a diff, and EF compares seed data by value.
    /// </summary>
    public static readonly DateTimeOffset SeededAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
