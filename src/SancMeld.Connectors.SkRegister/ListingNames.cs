using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.SkRegister;

/// <summary>A listed spelling split the way a register files a person: surname, and the given name.</summary>
public sealed record PersonName(string Surname, string? GivenName);

/// <summary>
/// What to ask a register for, from a listing's spellings. The lists write names every
/// way there is — "Aleksandr Anatolevich SURKOV", "SURKOV Aleksandr", "Сурков Александр";
/// ORSR files a person by surname and given name, and RPO a company by its whole name.
/// </summary>
public static partial class ListingNames
{
    /// <summary>
    /// Legal forms and filler words, which say nothing about which company it is.
    /// The list is the sieve's (pilot of 2026-10-18), in the folded form.
    /// </summary>
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "joint", "stock", "company", "public", "limited", "liability", "llc", "jsc", "pjsc", "ojsc", "cjsc", "ooo", "ao",
        "pao", "zao", "oao", "the", "of", "and", "society", "scientific", "production", "enterprise", "research", "institute",
        "federal", "state", "unitary", "commercial", "co", "ltd", "open", "closed", "non-profit", "nonprofit",
        "organisation", "organization", "autonomous", "group", "holding", "corporation", "corp", "inc", "trading", "house",
        "firm", "plant", "factory", "center", "centre", "scientific-production", "npo", "npp", "npk", "llp", "fze", "fzco",
        "l.l.c", "lc", "private", "general", "partnership", "association", "branch", "joint-stock", "fz-llc", "s.r.o", "a.s",
        "obschestvo", "ogranichennoj", "otvetstvennostju", "aktsionernoe", "publichnoe", "zakrytoe", "s", "sp", "z", "o.o",
    };

    /// <summary>
    /// Words that are in many company names and so prove nothing on their own: a Slovak
    /// "Digital Trust s.r.o." is not the listed "Digital Trust Bank". The pilot's noise.
    /// </summary>
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "bank", "investment", "industrial", "engineering", "service", "services", "trade", "technology", "technologies",
        "systems", "system", "logistics", "international", "export", "import", "energy", "development", "management",
        "consulting", "design", "electronics", "electronic", "works", "global", "supply", "trust", "post", "digital",
        "european", "europe", "capital", "invest", "finance", "financial", "solutions", "partners", "media", "consult",
        "transport", "industries", "industry", "construction", "security", "defence", "defense", "aviation", "marine",
        "shipping", "oil", "gas", "metal", "metals", "chemical", "machinery", "equipment", "components", "precision",
        "avangard", "zenit", "alfa", "alpha", "beta", "delta", "omega", "nova", "star", "standard", "union", "alliance",
    };

    /// <summary>The listed Latin spellings, and the Latin transliterations of the Cyrillic ones.</summary>
    public static IReadOnlyList<string> Latin(IEnumerable<string> names) =>
    [
        .. names
            .SelectMany(n => IsLatin(n) ? [n] : CyrillicTransliterator.Variants(n))
            .Select(n => Squeeze(n))
            .Where(n => n.Length > 0 && IsLatin(n))
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// Surname and given name of each spelling. A word in capitals among words that are not
    /// is the surname, as the EU and the UK write it; otherwise the last word is, as most
    /// registers write a name. The patronymic is left out: ORSR does not record one.
    /// </summary>
    public static IReadOnlyList<PersonName> Persons(IEnumerable<string> latinSpellings)
    {
        var result = new List<PersonName>();

        foreach (var spelling in latinSpellings)
        {
            var words = spelling.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Any(char.IsLetter)).ToList();
            if (words.Count == 0)
            {
                continue;
            }

            var capitals = words.Where(w => w.Length > 1 && w.Where(char.IsLetter).All(char.IsUpper)).ToList();
            var surname = capitals.Count == 1 && words.Count > 1 ? capitals[0] : words[^1];
            var given = words.FirstOrDefault(w => !ReferenceEquals(w, surname) && w != surname);

            result.Add(new PersonName(TitleCase(surname), given is null ? null : TitleCase(given)));
        }

        return [.. result.DistinctBy(p => (Fold(p.Surname), Fold(p.GivenName ?? string.Empty)))];
    }

    /// <summary>
    /// The words of a company name that identify it, folded: the quoted part if there is one
    /// ("Wildberries" in LLC "Wildberries Bank"), without legal forms and filler. Generic
    /// words stay in <see cref="Distinctive"/>'s input but never alone.
    /// </summary>
    public static IReadOnlyList<string> CoreWords(string name)
    {
        var quoted = Quoted().Match(name);
        var basis = quoted.Success ? quoted.Groups["q"].Value : name;

        return
        [
            .. Words().Split(basis)
                .Select(w => Fold(w).Trim('.', '"', '\'', '«', '»', '“', '”', '„'))
                .Where(w => w.Length > 1 && !Stop.Contains(w)),
        ];
    }

    /// <summary>The words that must all be in a Slovak company's name for it to carry the listed name.</summary>
    public static IReadOnlyList<string> Distinctive(string name) =>
        [.. CoreWords(name).Where(w => !Generic.Contains(w) && w.Length >= 3).Distinct(StringComparer.Ordinal)];

    /// <summary>Whether every word appears as a whole word in the name: "ak" is not in "Slovak".</summary>
    public static bool ContainsAllWords(string companyName, IReadOnlyCollection<string> words)
    {
        var folded = " " + string.Join(' ', Words().Split(Fold(companyName)).Where(w => w.Length > 0)) + " ";
        return words.Count > 0 && words.All(w => folded.Contains(" " + w + " ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Slovak legal forms and states, as spaced words of a folded name: "s. r. o." and
    /// "s.r.o." both become "s r o". A name carrying none is a sole trader, a club or an
    /// association — RPO holds them all — and not what a sanctioned company's namesake is.
    /// </summary>
    private static readonly string[] LegalForms =
    [
        " spolocnost s rucenim obmedzenym ", " akciova spolocnost ", " verejna obchodna spolocnost ",
        " komanditna spolocnost ", " jednoducha spolocnost na akcie ", " spol s r o ", " s r o ", " a s ",
        " v o s ", " k s ", " j s a ", " druzstvo ",
    ];

    private static readonly string[] States = [" v likvidacii ", " v konkurze ", " organizacna zlozka ", " odstepny zavod "];

    /// <summary>Whether the Slovak name is a company's: it carries a legal form.</summary>
    public static bool IsCompany(string name)
    {
        var spaced = Spaced(name);
        return LegalForms.Any(f => spaced.Contains(f, StringComparison.Ordinal));
    }

    /// <summary>
    /// The name without its legal form, state and punctuation: "TENZOR, s.r.o." → "tenzor".
    /// A one-word listed name must be the whole of this, or every "Vektor EU group" and
    /// "Profit Clean SK" in Slovakia is a lead.
    /// </summary>
    public static string CompanyCore(string name)
    {
        var spaced = Spaced(name);
        foreach (var part in LegalForms.Concat(States))
        {
            spaced = spaced.Replace(part, " ", StringComparison.Ordinal);
        }

        return string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Whether a Slovak company's name carries the listed one: the whole core for a
    /// one-word name, every distinctive word for a longer one.
    /// </summary>
    public static bool CarriesName(string companyName, IReadOnlyCollection<string> distinctive) =>
        IsCompany(companyName)
        && (distinctive.Count == 1
            ? CoreIsTheName(CompanyCore(companyName), distinctive.First())
            : ContainsAllWords(companyName, distinctive));

    /// <summary>
    /// How a foreign group names its Slovak company: the name alone, or the name and the
    /// country or region — "Finist Slovakia", "Wildberries SK", "Vektor Europe".
    /// </summary>
    private static readonly HashSet<string> CountrySuffixes = new(StringComparer.Ordinal)
    {
        "slovakia", "slovensko", "sk", "sr", "eu", "europe", "cee", "central europe",
    };

    private static bool CoreIsTheName(string core, string word) =>
        core == word || (core.StartsWith(word + " ", StringComparison.Ordinal) && CountrySuffixes.Contains(core[(word.Length + 1)..]));

    private static string Spaced(string name) => " " + string.Join(' ', NonAlphanumeric().Split(Fold(name)).Where(w => w.Length > 0)) + " ";

    /// <summary>Lower case without diacritics: "Mário" and "Mario" are one word to a search.</summary>
    public static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsLatin(string text) => !text.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    private static string Squeeze(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string TitleCase(string word) =>
        word.Length > 1 && word.Where(char.IsLetter).All(char.IsUpper)
            ? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
            : word;

    [GeneratedRegex("""["«“„](?<q>[^"»”“]+)["»”“]""")]
    private static partial Regex Quoted();

    [GeneratedRegex("""[\s,()/;:+&]+""")]
    private static partial Regex Words();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
