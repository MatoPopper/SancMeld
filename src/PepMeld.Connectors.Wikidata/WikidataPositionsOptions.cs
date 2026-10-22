using System.ComponentModel.DataAnnotations;
using PepMeld.Core.Domain;

namespace PepMeld.Connectors.Wikidata;

/// <summary>
/// A Wikidata position (a value of P39, "position held") read as a prominent public
/// function, or a whole family of them: every subclass of an item, or every subclass
/// within one jurisdiction, which is how the ministers of a country are reached when no
/// single item names them all.
/// </summary>
public sealed class WikidataPosition
{
    /// <summary>The position item, "Q18941264", or the class the positions descend from.</summary>
    [Required]
    public string Item { get; set; } = string.Empty;

    /// <summary>Read every position that is a subclass (P279) of <see cref="Item"/>, transitively, the item itself included.</summary>
    public bool IncludeSubclasses { get; set; }

    /// <summary>Only positions whose "applies to jurisdiction" (P1001) is this country item; implies subclasses.</summary>
    public string? Jurisdiction { get; set; }

    /// <summary>Only holders whose citizenship (P27) is this country item: the members of the European Parliament from one state.</summary>
    public string? Citizenship { get; set; }

    public PepCategory Category { get; set; }

    [Required]
    public string Institution { get; set; } = string.Empty;

    /// <summary>The role shown. Empty means the position's own label ("ministr financí České republiky").</summary>
    public string? Role { get; set; }

    /// <summary>
    /// The length of a term fixed by law, in years, when there is one. Wikidata often leaves
    /// a term open after it ended: on 2026-10-22 it had eleven members of the ČNB board open
    /// for seven seats, back to 2016. An open term older than this ends after it, and a
    /// later term of the same person in the same institution ends it earlier (a member
    /// appointed governor).
    /// </summary>
    [Range(1, 20)]
    public int? TermYears { get; set; }

    /// <summary>What names the snapshot entry and the function key: the item, and the filters when there are any.</summary>
    public string Key =>
        Item + (Jurisdiction is { Length: > 0 } j ? "-j" + j : string.Empty) + (Citizenship is { Length: > 0 } c ? "-c" + c : string.Empty);
}

/// <summary>Bound from "PepMeld:Connectors:Wikidata".</summary>
public sealed class WikidataPositionsOptions
{
    public const string SectionName = "PepMeld:Connectors:Wikidata";

    [Required]
    public string SparqlEndpoint { get; set; } = "https://query.wikidata.org/sparql";

    /// <summary>The query service asks for a pause between queries from one client.</summary>
    [Range(0, 60000)]
    public int DelayMilliseconds { get; set; } = 1000;

    /// <summary>
    /// The positions read per source, keyed by the source code. The defaults are the
    /// positions found on Wikidata on 2026-10-20 with holders and dates of birth; what is
    /// not here (Slovak constitutional judges, the Slovak prosecutor general, the NBS
    /// board, regional chairs in Slovakia) has no item on Wikidata yet. A new position is
    /// a configuration change, not code.
    /// </summary>
    public Dictionary<string, List<WikidataPosition>> Sources { get; set; } = new(StringComparer.Ordinal)
    {
        [WikidataPositionsConnector.CzechCode] = CzechPositions(),
        [WikidataPositionsConnector.SlovakCode] = SlovakPositions(),
    };

    public static List<WikidataPosition> CzechPositions() =>
    [
        new() { Item = "Q1819381", Category = PepCategory.HeadOfState, Institution = "Prezident České republiky", Role = "prezident republiky" },
        new() { Item = "Q83307", Jurisdiction = "Q213", Category = PepCategory.GovernmentMember, Institution = "Vláda České republiky" },
        new() { Item = "Q18941264", Category = PepCategory.MemberOfParliament, Institution = "Senát Parlamentu ČR", Role = "senátor", TermYears = 6 },
        new() { Item = "Q59773473", Category = PepCategory.SeniorJudge, Institution = "Ústavní soud ČR", Role = "soudce Ústavního soudu", TermYears = 10 },
        new() { Item = "Q140913203", Category = PepCategory.SeniorJudge, Institution = "Nejvyšší soud ČR", Role = "soudce Nejvyššího soudu" },
        new() { Item = "Q140913206", Category = PepCategory.SeniorJudge, Institution = "Nejvyšší správní soud ČR", Role = "soudce Nejvyššího správního soudu" },
        new() { Item = "Q28598459", Category = PepCategory.CentralBankBoard, Institution = "Česká národní banka", Role = "člen bankovní rady", TermYears = 6 },
        new() { Item = "Q25505764", Category = PepCategory.CentralBankBoard, Institution = "Česká národní banka", Role = "guvernér", TermYears = 6 },
        new() { Item = "Q26197430", Category = PepCategory.Prosecutor, Institution = "Nejvyšší státní zastupitelství", Role = "nejvyšší státní zástupce" },
        new() { Item = "Q1787679", IncludeSubclasses = true, Category = PepCategory.RegionalLeader, Institution = "kraj" },
        new() { Item = "Q64421048", IncludeSubclasses = true, Category = PepCategory.RegionalLeader, Institution = "statutární město" },
        new() { Item = "Q60038322", IncludeSubclasses = true, Category = PepCategory.Ambassador, Institution = "Ministerstvo zahraničních věcí ČR" },
        new() { Item = "Q27169", Citizenship = "Q213", Category = PepCategory.EuOrInternational, Institution = "Evropský parlament", Role = "poslanec Evropského parlamentu" },
    ];

    public static List<WikidataPosition> SlovakPositions() =>
    [
        new() { Item = "Q6468838", Category = PepCategory.HeadOfState, Institution = "Prezident Slovenskej republiky", Role = "prezident Slovenskej republiky" },
        new() { Item = "Q83307", Jurisdiction = "Q214", Category = PepCategory.GovernmentMember, Institution = "Vláda Slovenskej republiky" },
        new() { Item = "Q109535611", Category = PepCategory.CentralBankBoard, Institution = "Národná banka Slovenska", Role = "guvernér" },
        new() { Item = "Q30249299", IncludeSubclasses = true, Category = PepCategory.Ambassador, Institution = "Ministerstvo zahraničných vecí a európskych záležitostí SR" },
        new() { Item = "Q18249941", Category = PepCategory.RegionalLeader, Institution = "hlavné mesto Bratislava", Role = "primátor" },
        new() { Item = "Q27169", Citizenship = "Q214", Category = PepCategory.EuOrInternational, Institution = "Európsky parlament", Role = "poslanec Európskeho parlamentu" },
    ];
}
