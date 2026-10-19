namespace SancMeld.Core.Access;

/// <summary>
/// Whether whoever the current request is for may see the additional information: the
/// links of <see cref="Relations.RelationSystems.AdditionalInfo"/>, which rest on a name
/// alone or on an analyst's notes.
///
/// Asked by the queries, not by the endpoints, so that no path to a related party — the
/// graph, the feed, a check, a party search — can forget it. The host decides who the
/// caller is; the core only knows the question.
/// </summary>
public interface IAdditionalInfoAccess
{
    ValueTask<bool> AllowedAsync(CancellationToken ct = default);
}

/// <summary>The default: nobody sees it. A host without users has nobody to show it to.</summary>
public sealed class NoAdditionalInfoAccess : IAdditionalInfoAccess
{
    public ValueTask<bool> AllowedAsync(CancellationToken ct = default) => ValueTask.FromResult(false);
}

/// <summary>Everyone sees it: the operator's console, a test.</summary>
public sealed class FullAdditionalInfoAccess : IAdditionalInfoAccess
{
    public ValueTask<bool> AllowedAsync(CancellationToken ct = default) => ValueTask.FromResult(true);
}
