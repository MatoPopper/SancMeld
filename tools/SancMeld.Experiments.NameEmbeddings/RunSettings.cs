namespace SancMeld.Experiments.NameEmbeddings;

/// <summary>Which ground truth a run is measured on.</summary>
public enum Mode
{
    /// <summary>
    /// The links publishers declare between their lists. Two authorities, two
    /// transliterations of the same subject — but both in the Latin alphabet nearly every
    /// time, so this says little about other scripts.
    /// </summary>
    Xref,

    /// <summary>
    /// Listings that carry a spelling in another script alongside a Latin one. The other
    /// script is hidden from the index and used as the query: can the listing be reached
    /// through its Latin spellings alone? That is the position of every listing the
    /// publisher recorded in one script only, when a caller has the other.
    /// </summary>
    CrossScript,
}

/// <summary>What one run measures, from the command line.</summary>
public sealed record RunSettings
{
    /// <summary>An Ollama model name, exactly as <c>ollama pull</c> took it.</summary>
    public required string Model { get; init; }

    public Mode Mode { get; init; } = Mode.Xref;

    public Uri Endpoint { get; init; } = new("http://localhost:11434");

    /// <summary>
    /// The k in recall@k, and how many candidates each ranking contributes to the fusion.
    /// 25 matches what the corpus seeder asks the trigram search for, so the baseline here
    /// is the baseline the corpus was built against.
    /// </summary>
    public int Candidates { get; init; } = 25;

    /// <summary>
    /// Some models are trained with a role prefix — multilingual-e5 expects "query: " on
    /// one side and "passage: " on the other, and measures noticeably worse without them.
    /// Others want nothing. The prefix is part of the cache key, so changing it re-embeds.
    /// </summary>
    public string QueryPrefix { get; init; } = "";

    public string NamePrefix { get; init; } = "";

    /// <summary>Only links into this list, e.g. <c>UN_SC</c>. Default: every list.</summary>
    public string? TargetSource { get; init; }

    /// <summary>First N links, for a quick run while something is still being wired up.</summary>
    public int? Limit { get; init; }

    public string CacheDirectory { get; init; } = "cache";

    public string ResultsDirectory { get; init; } = "results";

    public const string Usage = """
        Measures whether a multilingual embedding model reaches cross-list pairs that the
        trigram search does not. Needs a migrated database with the lists imported, and an
        Ollama instance with the model pulled.

        Usage:
          dotnet run -- --model <name> [options]

        Options:
          --model <name>          Ollama model, e.g. bge-m3 (required)
          --mode xref|cross-script
                                  Ground truth: declared cross-references (default), or
                                  listings' non-Latin spellings held out and used as queries
          --endpoint <url>        Ollama base URL          (default http://localhost:11434)
          --candidates <k>        k for recall@k and fusion (default 25)
          --query-prefix <text>   Prepended to every query  (e5 models: "query: ")
          --name-prefix <text>    Prepended to every name   (e5 models: "passage: ")
          --target <source>       Only links into one list, e.g. UN_SC
          --limit <n>             Only the first n links
          --cache <dir>           Where embeddings are kept between runs (default cache)
          --results <dir>         Where the per-run JSON goes             (default results)

        Examples:
          dotnet run -- --model bge-m3
          dotnet run -- --model zylonai/multilingual-e5-large --query-prefix "query: " --name-prefix "passage: "
        """;

    public static RunSettings? Parse(string[] args)
    {
        string? model = null;
        var settings = new RunSettings { Model = "" };

        for (var i = 0; i < args.Length; i++)
        {
            string Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{args[i]} needs a value.");
                }

                return args[++i];
            }

            switch (args[i])
            {
                case "--model": model = Next(); break;
                case "--mode":
                    settings = settings with
                    {
                        Mode = Next() switch
                        {
                            "xref" => Mode.Xref,
                            "cross-script" => Mode.CrossScript,
                            var other => throw new ArgumentException($"Unknown mode '{other}'. Use xref or cross-script."),
                        },
                    };
                    break;
                case "--endpoint": settings = settings with { Endpoint = new Uri(Next()) }; break;
                case "--candidates": settings = settings with { Candidates = int.Parse(Next()) }; break;
                case "--query-prefix": settings = settings with { QueryPrefix = Next() }; break;
                case "--name-prefix": settings = settings with { NamePrefix = Next() }; break;
                case "--target": settings = settings with { TargetSource = Next() }; break;
                case "--limit": settings = settings with { Limit = int.Parse(Next()) }; break;
                case "--cache": settings = settings with { CacheDirectory = Next() }; break;
                case "--results": settings = settings with { ResultsDirectory = Next() }; break;
                case "--help" or "-h" or "-?": return null;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return settings with { Model = model };
    }
}
