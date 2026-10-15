using SancMeld.Core.Domain;

namespace SancMeld.Connectors.CzMzv;

/// <summary>
/// What an entry cites, read for two things.
///
/// The EU act whose criteria the subject met says which regime the listing belongs to,
/// and the code used is the one the EU list itself uses for the same act — UKR for
/// Decision 2014/145/CFSP and Regulation 269/2014, HR for the global human rights
/// regime, RUSDA for Russia's destabilising activities, TERR for the terrorism
/// measures — so a Czech listing lands beside its EU counterparts.
///
/// The measures column names articles, not measures: "čl. 2 nařízení Rady (EU) č.
/// 269/2014" is the asset freeze and the prohibition on making funds available, "čl. 2
/// … rozhodnutí 2020/1999" the entry ban and "čl. 3" of the same decision the freeze.
/// Only articles whose content is known are typed; the Czech act's own paragraphs
/// ("§ 8a písm. a) až c) zákona č. 69/2006 Sb.") stay as text in Remarks, and an entry
/// that cites nothing typeable is recorded as Other rather than as nothing.
/// </summary>
internal static class CzMeasures
{
    public static (List<MeasureType> Measures, List<string> Programmes) Parse(string euProvision, string measures)
    {
        var cited = euProvision + " " + measures;
        var programmes = new List<string>();

        void Programme(string code)
        {
            if (!programmes.Contains(code))
            {
                programmes.Add(code);
            }
        }

        if (Has(cited, "2014/145") || Has(cited, "269/2014"))
        {
            Programme("UKR");
        }

        if (Has(cited, "2020/1999") || Has(cited, "2020/1998"))
        {
            Programme("HR");
        }

        if (Has(cited, "2024/2642") || Has(cited, "2024/2643"))
        {
            Programme("RUSDA");
        }

        if (Has(cited, "2001/931") || Has(cited, "2580/2001") || Has(cited, "2026/455") || Has(cited, "2026/456"))
        {
            Programme("TERR");
        }

        var typed = new List<MeasureType>();

        void Measure(MeasureType kind)
        {
            if (!typed.Contains(kind))
            {
                typed.Add(kind);
            }
        }

        // Article 2 of the Ukraine and Russia regulations, and of the terrorism
        // instruments: funds frozen and none to be made available.
        if (Has(measures, "čl. 2 nařízení Rady (EU) č. 269/2014")
            || Has(measures, "čl. 2 nařízení Rady (SZBP) č. 2024/2642")
            || Has(measures, "2001/931")
            || Has(measures, "2026/455"))
        {
            Measure(MeasureType.AssetFreeze);
            Measure(MeasureType.FundsProhibition);
        }

        if (Has(measures, "2020/1999"))
        {
            // Decision (CFSP) 2020/1999: Article 2 restricts entry, Article 3 freezes.
            if (Has(measures, "čl. 2 odst. 1"))
            {
                Measure(MeasureType.EntryBan);
            }

            if (Has(measures, "čl. 3"))
            {
                Measure(MeasureType.AssetFreeze);
                Measure(MeasureType.FundsProhibition);
            }
        }

        // § 4 odst. 2 of Act 69/2006 lists every area a sanction may cover, a) to k):
        // funds, trade, services, entry, transport and the rest. An entry citing the
        // whole range is under all of them.
        if (Has(measures, "§ 4 odst. 2 písm. a) až k)"))
        {
            Measure(MeasureType.AssetFreeze);
            Measure(MeasureType.FundsProhibition);
            Measure(MeasureType.EntryBan);
            Measure(MeasureType.TradeRestriction);
            Measure(MeasureType.ServicesRestriction);
        }

        if (typed.Count == 0 && measures.Trim().Length > 0)
        {
            typed.Add(MeasureType.Other);
        }

        return (typed, programmes);
    }

    private static bool Has(string text, string needle) =>
        text.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
