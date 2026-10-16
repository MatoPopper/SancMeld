using System.Globalization;
using System.Text;

namespace SancMeld.Connectors.Wikidata;

/// <summary>
/// The four questions this connector asks, as SPARQL. Kept as text in one place so a
/// reader can paste each into query.wikidata.org and see what comes back.
/// </summary>
internal static class WikidataQueries
{
    /// <summary>
    /// The relations followed, and the direction that makes the listed person the subject
    /// of the sentence. P1830 "owner of" points from the person; P112 "founded by" points
    /// at them. Both are read, and the mapper turns each into the kind from the person's
    /// side.
    /// </summary>
    public static readonly string[] RelationProperties =
        ["P1830", "P127", "P749", "P355", "P112", "P169", "P488", "P3320", "P1037"];

    /// <summary>Register identifiers worth carrying: what an officer can look up in a company register.</summary>
    public static readonly IReadOnlyDictionary<string, string> IdentifierProperties = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["P1278"] = "LEI",
        ["P2622"] = "Companies House",
        ["P7011"] = "OGRN",
        ["P3125"] = "EDRPOU",
        ["P4156"] = "IČO (CZ)",
        ["P1320"] = "OpenCorporates",
    };

    /// <summary>
    /// Every human born on one day, with every label and alias in every language.
    ///
    /// The precision check matters: a person the register knows was born "in 1950" is
    /// stored with the first of January as the simple value, and without it every New
    /// Year's Day would pull in a whole year's worth of births (582 478 rows for ten
    /// dates, measured 2026-10-16). All languages, not a list: the listed spelling may be
    /// in Cyrillic, Arabic or Georgian, and so may the label that matches it.
    /// </summary>
    public static string PersonsBornOn(DateOnly date)
    {
        var literal = $"\"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T00:00:00Z\"^^xsd:dateTime";

        return $$"""
            SELECT ?item ?label (LANG(?label) AS ?lang) WHERE {
              ?item wdt:P569 {{literal}} ; wdt:P31 wd:Q5 .
              ?item p:P569/psv:P569 ?dob .
              ?dob wikibase:timeValue {{literal}} ; wikibase:timePrecision 11 .
              { ?item rdfs:label ?label } UNION { ?item skos:altLabel ?label }
            }
            """;
    }

    /// <summary>
    /// Which items the followed properties tie to each of the given items, in either
    /// direction, as the register's "truthy" view: the best-ranked statements, which is
    /// what the item page shows and what leaves deprecated ones out.
    ///
    /// Discovery only — the statement nodes, their qualifiers and their sources come from
    /// <see cref="StatementDetails"/> once both ends are known. Asking for all of it in
    /// one query, through the statement nodes, timed out at sixty seconds every time on
    /// six items (2026-10-16); this form answers the same six in 1.8 s.
    /// </summary>
    public static string RelationsOf(IEnumerable<string> itemIds)
    {
        var blocks = string.Join(Union, RelationProperties.Select(p => $$"""
                          { ?item wdt:{{p}} ?target . BIND("{{p}}" AS ?prop) BIND("out" AS ?dir) }
                          UNION
                          { ?target wdt:{{p}} ?item . BIND("{{p}}" AS ?prop) BIND("in" AS ?dir) }
                          """));

        return $$"""
            SELECT ?item ?prop ?dir ?target WHERE {
              VALUES ?item { {{Values(itemIds)}} }
              {{blocks}}
              FILTER(isIRI(?target) && STRSTARTS(STR(?target), "http://www.wikidata.org/entity/Q"))
            }
            """;
    }

    /// <summary>
    /// The statement behind each known tie: its id, when it held, what share, and what
    /// the register cites for it. Both ends bound, so each block is a lookup; 0.3 s for
    /// a pair (2026-10-16).
    /// </summary>
    public static string StatementDetails(IEnumerable<RelationEnd> ties)
    {
        var groups = ties
            .GroupBy(t => (t.Property, t.Outgoing))
            .Select(g =>
            {
                var pairs = string.Join(' ', g.Select(t => $"(wd:{Id(t.Item)} wd:{Id(t.Target)})"));
                var (property, outgoing) = g.Key;
                var pattern = outgoing
                    ? $"?item p:{property} ?st . ?st ps:{property} ?target ."
                    : $"?target p:{property} ?st . ?st ps:{property} ?item .";

                return $$"""
                          { VALUES (?item ?target) { {{pairs}} } {{pattern}} BIND("{{property}}" AS ?prop) BIND("{{(outgoing ? "out" : "in")}}" AS ?dir) }
                          """;
            });

        return $$"""
            SELECT ?item ?prop ?dir ?target ?st ?start ?end ?share ?ref WHERE {
              {{string.Join(Union, groups)}}
              OPTIONAL { ?st pq:P580 ?start }
              OPTIONAL { ?st pq:P582 ?end }
              OPTIONAL { ?st pq:P1107 ?share }
              OPTIONAL { ?st prov:wasDerivedFrom ?refNode . ?refNode pr:P854 ?ref }
            }
            """;
    }

    /// <summary>
    /// What each party is, its English label and description, and its country.
    ///
    /// Organisation is read off the properties an organisation carries — legal form,
    /// industry, headquarters, parent, chief executive, chairperson, director — rather
    /// than off the class tree: `wdt:P31/wdt:P279* wd:Q43229` took 15.7 s on four items
    /// (2026-10-16), because a country is an instance of a great many things. A ship is
    /// an instance of a direct subclass of ship, which catches the registered kinds.
    /// </summary>
    public static string PartyDetails(IEnumerable<string> itemIds) => $$"""
        SELECT ?item ?enLabel ?desc ?iso ?human ?org ?ship WHERE {
          VALUES ?item { {{Values(itemIds)}} }
          OPTIONAL { ?item rdfs:label ?enLabel FILTER(LANG(?enLabel) = "en") }
          OPTIONAL { ?item schema:description ?desc FILTER(LANG(?desc) = "en") }
          OPTIONAL { ?item wdt:P17 ?country . ?country wdt:P297 ?iso }
          OPTIONAL { ?item wdt:P31 wd:Q5 . BIND(true AS ?human) }
          OPTIONAL { ?item wdt:P1454|wdt:P452|wdt:P159|wdt:P749|wdt:P169|wdt:P488|wdt:P1037 ?orgSignal . BIND(true AS ?org) }
          OPTIONAL { ?item wdt:P31 ?shipClass . ?shipClass wdt:P279 wd:Q11446 . BIND(true AS ?ship) }
        }
        """;

    public static string PartyIdentifiers(IEnumerable<string> itemIds)
    {
        var props = string.Join(' ', IdentifierProperties.Keys.Select(p => "wdt:" + p));

        return $$"""
            SELECT ?item ?prop ?value WHERE {
              VALUES ?item { {{Values(itemIds)}} }
              VALUES ?prop { {{props}} }
              ?item ?prop ?value .
            }
            """;
    }

    /// <summary>Every label and alias of each party, in every language: the names a screening can reach it by.</summary>
    public static string PartyNames(IEnumerable<string> itemIds) => $$"""
        SELECT ?item ?label (LANG(?label) AS ?lang) WHERE {
          VALUES ?item { {{Values(itemIds)}} }
          { ?item rdfs:label ?label } UNION { ?item skos:altLabel ?label }
        }
        """;

    /// <summary>Between two UNION blocks, indented like the query text around them.</summary>
    private const string Union = "\n              UNION\n";

    private static string Values(IEnumerable<string> itemIds)
    {
        var sb = new StringBuilder();
        foreach (var id in itemIds)
        {
            sb.Append("wd:").Append(Id(id)).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Only ever our own stored ids, but a VALUES clause is still SPARQL text.</summary>
    private static string Id(string id) =>
        id.Length >= 2 && id[0] == 'Q' && id.Skip(1).All(char.IsAsciiDigit)
            ? id
            : throw new ArgumentException($"'{id}' is not a Wikidata item id.", nameof(id));
}

/// <summary>One tie found by discovery, to be looked up as a statement.</summary>
public sealed record RelationEnd(string Item, string Property, bool Outgoing, string Target);
