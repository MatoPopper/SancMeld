using System.ComponentModel.DataAnnotations;

namespace SancMeld.Core.Configuration;

/// <summary>
/// Configuration bound from the "SancMeld" section, validated at startup.
///
/// The connection string is deliberately NOT part of this class: it lives under
/// the standard "ConnectionStrings:SancMeld" key so it can be supplied by user
/// secrets or an environment variable without ever touching a tracked file.
/// </summary>
public sealed class SancMeldOptions
{
    public const string SectionName = "SancMeld";

    public IngestOptions Ingest { get; set; } = new();
    public MatchingOptions Matching { get; set; } = new();
    public EmbeddingsOptions Embeddings { get; set; } = new();
}

/// <summary>
/// How far the embedding model is allowed into screening.
///
/// <see cref="Shadow"/> exists because a second candidate generator can only be judged on
/// real queries, and the measurement that justified it (tools/SancMeld.Experiments.
/// NameEmbeddings) was made on publishers' spellings, not on what callers type. In shadow
/// the embedding's candidates are retrieved and recorded with every screening, and change
/// nothing anyone sees — what they would have added is then a query over
/// screening_candidate rather than an argument.
/// </summary>
public enum EmbeddingsMode
{
    /// <summary>No model is called, nothing is embedded at import, nothing is recorded.</summary>
    Off,

    /// <summary>
    /// Candidates the embedding reaches are recorded as retrieved_by = semantic, below
    /// everything shown, and never surface. Scores are unaffected.
    /// </summary>
    Shadow,
}

/// <summary>
/// The embedding model beside the trigram: a second way to reach a listing, for the names
/// a character-level index cannot — another script, a translation.
///
/// Measured before it was built: on 7 981 listings whose non-Latin spelling was hidden and
/// used as the query, the trigram reaches 0 % and bge-m3 96.3 % at k = 25, and fusing the
/// two lost nothing the trigram had. See tools/SancMeld.Experiments.NameEmbeddings.
/// </summary>
public sealed class EmbeddingsOptions
{
    public EmbeddingsMode Mode { get; set; } = EmbeddingsMode.Off;

    /// <summary>An Ollama-compatible /api/embed endpoint. In-process ONNX is the planned successor behind the same interface.</summary>
    [Required]
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Matches embedding_model.code. The row, not this setting, says which model file and
    /// how many dimensions — a vector is only comparable to vectors from the same model,
    /// and the table is what ties each stored vector to the one that produced it.
    /// </summary>
    [Required]
    public string ModelCode { get; set; } = "BGE_M3";

    /// <summary>
    /// How many listings the embedding contributes. 25 is where the measurement was made;
    /// recall@25 was 96.3 % and recall@10 94.7 %, so the last fifteen buy little and cost
    /// fifteen rows per screening.
    /// </summary>
    [Range(1, 200)]
    public int Candidates { get; set; } = 25;

    /// <summary>Some models are trained with a role prefix (e5: "query: " / "passage: "). bge-m3 wants none.</summary>
    public string QueryPrefix { get; set; } = "";

    public string NamePrefix { get; set; } = "";

    /// <summary>Names per request when embedding at import. Small, because a CPU host serves other things too.</summary>
    [Range(1, 256)]
    public int BatchSize { get; set; } = 32;

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class IngestOptions
{
    /// <summary>
    /// Where raw downloaded artefacts are kept. Every import stores the file it
    /// actually received, so a past decision can be reconstructed and a parser
    /// bug can be fixed retroactively without re-downloading.
    /// </summary>
    [Required]
    public string SnapshotStoragePath { get; set; } = "./.snapshots";

    /// <summary>
    /// Sent with every download. Not optional: OFAC's Sanctions List Service
    /// returns HTTP 403 to requests without a User-Agent, and .NET's HttpClient
    /// sends none by default.
    /// </summary>
    [Required, MinLength(3)]
    public string UserAgent { get; set; } = "SancMeld/1.0 (+https://github.com/MatoPopper/SancMeld)";

    [Range(1, 3600)]
    public int DownloadTimeoutSeconds { get; set; } = 600;

    [Range(0, 10)]
    public int DownloadRetryCount { get; set; } = 3;
}

public sealed class MatchingOptions
{
    /// <summary>
    /// pg_trgm similarity threshold for candidate generation.
    ///
    /// Lower = wider net = higher recall = more analyst work. In sanctions
    /// screening a missed match is a legal breach while a false positive only
    /// costs time, so err low. Measured against the full EU list, 0.30-0.35
    /// reaches 100% recall@10.
    /// </summary>
    [Range(0.0, 1.0)]
    public double SimilarityThreshold { get; set; } = 0.35;

    /// <summary>
    /// pg_trgm word_similarity threshold, applied alongside <see cref="SimilarityThreshold"/>
    /// rather than instead of it.
    ///
    /// similarity() divides shared trigrams by the trigrams of *both* strings, so its
    /// denominator grows with the length of the recorded name. A surname typed on its own
    /// therefore scores low against a long entry for no reason having to do with whether
    /// it is the right person: measured on 200 subjects, searching by surname found 53.5 %
    /// of them and by a surname clipped by two characters 13.0 %. word_similarity compares
    /// the query against the best-matching *word*, which does not have that bias — the same
    /// two searches reach 98.5 % and 98.0 %.
    ///
    /// Full names are unaffected (100 % either way, 15.4 vs 15.5 candidates on average),
    /// because this only ever adds candidates. It cannot lose one the trigram branch finds.
    ///
    /// 0.6 is where the measurement stops improving: 0.5 found nothing extra on that sample
    /// and returned about half again as many candidates.
    /// </summary>
    [Range(0.0, 1.0)]
    public double WordSimilarityThreshold { get; set; } = 0.6;

    /// <summary>
    /// Shortest query the word_similarity branch runs for.
    ///
    /// Below this it stops being a search. "al" matches a word in 1 782 subjects and "li"
    /// in 343, which is not a candidate set anybody can act on — and with MaxCandidates
    /// capping the result, the right subject is as likely to be cut as kept. The trigram
    /// branch still runs at any length.
    /// </summary>
    [Range(1, 50)]
    public int MinWordSimilarityQueryLength { get; set; } = 4;

    [Range(1, 1000)]
    public int MaxCandidates { get; set; } = 100;

    /// <summary>
    /// Score at or above which a candidate is presented as a hit.
    ///
    /// A different question from SimilarityThreshold, and the two must not be conflated:
    /// that one decides what the database retrieves, this one decides what an analyst is
    /// asked to look at. Candidates below it are still recorded — they are the negative
    /// half of any future training set, and a system that never stores them can only ever
    /// learn from what it already shows.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MatchThreshold { get; set; } = 0.50;

    /// <summary>
    /// Whether generic aliases ("Abu Ahmad") take part in primary scoring.
    /// Off by default, matching OFAC's own guidance: weak aliases are meant to
    /// corroborate a hit raised by other evidence, not to raise one.
    /// </summary>
    public bool ScoreWeakAliases { get; set; }
}
