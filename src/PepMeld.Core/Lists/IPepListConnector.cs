using PepMeld.Core.Domain;

namespace PepMeld.Core.Lists;

/// <summary>
/// A source of persons in prominent public functions. One implementation per publisher,
/// in its own project under src/PepMeld.Connectors.*, as SancMeld's sanctions connectors.
///
/// Download and parse are separate on purpose: the bytes are kept as a snapshot before
/// anything interprets them, so a mapping bug found later can be replayed against the
/// file a decision was made on.
/// </summary>
public interface IPepListConnector
{
    /// <summary>Matches pep_source.code.</summary>
    string SourceCode { get; }

    /// <summary>Fetches the current file. Always sends a User-Agent.</summary>
    Task<ListDownload> DownloadAsync(CancellationToken ct);

    /// <summary>
    /// Reads a downloaded file into persons with their functions. Only persons with at
    /// least one function this product cares about are returned; a parliament's file also
    /// lists its committee clerks. The stream is seekable (a snapshot file).
    /// </summary>
    IReadOnlyList<ParsedPerson> Parse(Stream content);
}

/// <summary>The bytes and where they came from. Dispose it to release the HTTP response.</summary>
public sealed class ListDownload(Stream content, string url, IDisposable? owner) : IDisposable
{
    public Stream Content { get; } = content;
    public string Url { get; } = url;

    public void Dispose()
    {
        Content.Dispose();
        owner?.Dispose();
    }
}

/// <summary>A person as a source records them, with their relevant functions. Names come without titles; the titles come separately.</summary>
public sealed record ParsedPerson(
    string SourceKey,
    string GivenName,
    string FamilyName,
    string? TitleBefore,
    string? TitleAfter,
    DateOnly? BirthDate,
    DateOnly? DeathDate,
    string SourceUrl,
    IReadOnlyList<ParsedFunction> Functions)
{
    public string DisplayName => string.Join(' ', new[] { GivenName, FamilyName }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Town of residence where the source publishes it: a second detail to tell namesakes apart when no date of birth is there.</summary>
    public string? Residence { get; init; }
}

public sealed record ParsedFunction(
    string SourceKey,
    Country Country,
    PepCategory Category,
    string Institution,
    string Role,
    DateOnly? From,
    DateOnly? To,
    string? SourceUrl)
{
    /// <summary>
    /// What the dates are when they are not the person's own: a parliament that lists
    /// its members per term gives the term's dates, not a substitute's. Shown with the
    /// finding, so the twelve months are not counted from a date that is only a bound.
    /// </summary>
    public string? DateNote { get; init; }
}
