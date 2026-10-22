using System.Globalization;
using System.Text.Json;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;
using PepMeld.Core.Matching;

namespace PepMeld.Connectors.CzRpp;

/// <summary>
/// Reads the heads ("osoba v čele") of the authorities the Czech PEP list names out of the
/// register of public authorities.
///
/// Which authorities, by the register's own categories, as FAÚ's guidance no. 7 of
/// 17 July 2024 lists the offices (annex 1, a closed list): a region and Prague (KO12, the
/// hejtman or the mayor of Prague), a municipality with extended powers (KO11, the mayor;
/// the 26 statutory cities among them, KO103, have a primátor), the districts Praha 1 to 22
/// (KO241, the mayor of the district, named in the guidance since 2024), and a ministry or
/// another central administrative office (KO129, the minister or the head). The guidance
/// names deputies, secretaries and directors too; the register has only the head, so they
/// are not here.
///
/// The register gives the head's name and the address of residence, nothing else: no date
/// of birth, no date the office began. The address is not read. A head who is no longer in
/// the file is ended by the import as of that day, which is as precise as the register.
/// </summary>
internal static class OvmMapper
{
    /// <summary>Where a person can see the authority and its head.</summary>
    internal const string CatalogueUrl = "https://rpp-ais.egon.gov.cz/AISP/verejne/ovm-spuu/katalog-ovm/";

    private const string DateNote = "Rejstřík OVM neuvádza začiatok funkcie; osoba je v čele orgánu podľa rejstříku k dátumu importu.";

    private enum Kind
    {
        Region,
        Prague,
        StatutoryCity,
        ExtendedMunicipality,
        PragueDistrict,
        Ministry,
        CentralOffice,
    }

    public static IReadOnlyList<ParsedPerson> Map(Stream content)
    {
        using var document = JsonDocument.Parse(content);
        var persons = new List<ParsedPerson>();

        foreach (var authority in document.RootElement.GetProperty("položky").EnumerateArray())
        {
            if (authority.TryGetProperty("ukončení", out _))
            {
                continue;
            }

            var categories = Categories(authority);
            var name = authority.GetProperty("název").GetProperty("cs").GetString() ?? string.Empty;
            if (Classify(categories, name) is not { } kind)
            {
                continue;
            }

            var id = authority.GetProperty("identifikátor").GetString()!;
            if (!authority.TryGetProperty("osoba-v-čele", out var heads))
            {
                continue;
            }

            foreach (var head in heads.EnumerateArray())
            {
                var (given, family) = Split(head.GetProperty("jméno").GetString() ?? string.Empty);
                if (family.Length == 0)
                {
                    continue;
                }

                var institution = Institution(name);
                var url = CatalogueUrl + id;
                var person = new ParsedPerson(
                    Key(id, given, family),
                    given,
                    family,
                    TitleBefore: null,
                    TitleAfter: null,
                    BirthDate: null,
                    DeathDate: null,
                    url,
                    [
                        new ParsedFunction(
                            "ovm:" + id,
                            Country.CZ,
                            kind is Kind.Ministry or Kind.CentralOffice ? PepCategory.GovernmentMember : PepCategory.RegionalLeader,
                            institution,
                            Role(kind),
                            From: null,
                            To: null,
                            url) { DateNote = DateNote },
                    ]);

                persons.Add(person);
            }
        }

        return persons;
    }

    private static HashSet<string> Categories(JsonElement authority)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        if (authority.TryGetProperty("seznam-kategorií", out var list))
        {
            foreach (var item in list.EnumerateArray())
            {
                var reference = item.GetProperty("kategorie").GetString() ?? string.Empty;
                codes.Add(reference[(reference.LastIndexOf('/') + 1)..]);
            }
        }

        return codes;
    }

    private static Kind? Classify(HashSet<string> categories, string name)
    {
        if (categories.Contains("KO12"))
        {
            return name.Contains("PRAHA", StringComparison.OrdinalIgnoreCase) ? Kind.Prague : Kind.Region;
        }

        if (categories.Contains("KO103"))
        {
            return Kind.StatutoryCity;
        }

        if (categories.Contains("KO11"))
        {
            return Kind.ExtendedMunicipality;
        }

        if (categories.Contains("KO241"))
        {
            return Kind.PragueDistrict;
        }

        if (categories.Contains("KO528"))
        {
            return Kind.Ministry;
        }

        // KO129 is the ministries and the other central offices together; what is left of
        // it after KO528 are the offices of § 2 of Act 2/1969.
        if (categories.Contains("KO129") || categories.Contains("KO104"))
        {
            return Kind.CentralOffice;
        }

        return null;
    }

    /// <summary>
    /// The title as FAÚ's list words it. The first word is what lending a date of birth
    /// compares with another list's role ("primátor" and Wikidata's "primátor Brna").
    /// </summary>
    private static string Role(Kind kind) => kind switch
    {
        Kind.Region => "hejtman",
        Kind.Prague => "primátor hlavního města Prahy",
        Kind.StatutoryCity => "primátor",
        Kind.ExtendedMunicipality => "starosta obce s rozšířenou působností",
        Kind.PragueDistrict => "starosta městské části",
        Kind.Ministry => "ministr",
        _ => "vedoucí ústředního orgánu státní správy",
    };

    /// <summary>The register writes Prague in capitals; everything else as it is.</summary>
    private static string Institution(string name) =>
        name.Equals(name.ToUpperInvariant(), StringComparison.Ordinal) ? Capitalise(name) : name;

    /// <summary>
    /// "PETRA PECKOVÁ" to Petra and Pecková; with three words the first is the given name and
    /// the rest the surname ("IVETA KOČÍ PALKOVSKÁ"), which is how Czech double surnames go.
    /// </summary>
    internal static (string Given, string Family) Split(string raw)
    {
        var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Capitalise).ToArray();
        return words.Length switch
        {
            0 => (string.Empty, string.Empty),
            1 => (string.Empty, words[0]),
            _ => (words[0], string.Join(' ', words[1..])),
        };
    }

    private static string Capitalise(string text)
    {
        var lower = text.ToLower(CultureInfo.GetCultureInfo("cs-CZ"));
        var chars = lower.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 0 || chars[i - 1] is ' ' or '-')
            {
                chars[i] = char.ToUpper(chars[i], CultureInfo.GetCultureInfo("cs-CZ"));
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// The register has no identifier for a person, so the key is the authority and the
    /// name: the same head on the next day is the same row, a new head is a new one, and the
    /// old head's office ends when they leave the file.
    /// </summary>
    private static string Key(string authorityId, string given, string family) =>
        $"v1:{authorityId}:{PersonNames.Normalize(given + " " + family)}";
}
