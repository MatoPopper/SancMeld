using PepMeld.Core.Domain;

namespace PepMeld.Core.Lists;

public sealed record ImportOutcome(
    string SourceCode,
    ImportStatus Status,
    TimeSpan Duration,
    int PersonsSeen,
    int PersonsInserted,
    int PersonsUpdated,
    int FunctionsSeen,
    int FunctionsInserted,
    int FunctionsUpdated,
    int FunctionsEnded,
    string? Reason);
