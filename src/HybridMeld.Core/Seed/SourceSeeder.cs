using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace HybridMeld.Core.Seed;

/// <summary>
/// The official sources worth watching from day one, checked by hand on 2026-10-21:
/// which of them publish a feed, which only a page, and which refuse a program
/// altogether (the Council's press site sits behind a browser check, so it is Manual).
/// Idempotent: a source is found by its code and never overwritten, so a user's edits
/// (interval, enabled) survive a re-run; only missing ones are added.
/// </summary>
public sealed class SourceSeeder(HybridMeldDbContext db, TimeProvider clock)
{
    public static readonly IReadOnlyList<Source> Defaults =
    [
        new()
        {
            Code = "EUVSDISINFO", Name = "EUvsDisinfo", Publisher = "EEAS East StratCom Task Force",
            Url = "https://euvsdisinfo.eu/feed/", Kind = SourceKind.Official, FetchKind = FetchKind.Rss, IsOfficial = true,
            Licence = "EEAS, reuse with attribution", RefreshHours = 24, AddedBy = "seed",
        },
        new()
        {
            Code = "UK_FCDO_ATOM", Name = "UK FCDO announcements", Publisher = "Foreign, Commonwealth & Development Office",
            Url = "https://www.gov.uk/government/organisations/foreign-commonwealth-development-office.atom",
            Kind = SourceKind.Official, FetchKind = FetchKind.Rss, IsOfficial = true, Licence = "Open Government Licence v3.0", RefreshHours = 24, AddedBy = "seed",
        },
        new()
        {
            Code = "UK_NCSC", Name = "UK NCSC news and advisories", Publisher = "National Cyber Security Centre",
            Url = "https://www.ncsc.gov.uk/api/1/services/v1/all-rss-feed.xml",
            Kind = SourceKind.Official, FetchKind = FetchKind.Rss, IsOfficial = true, Licence = "Open Government Licence v3.0", RefreshHours = 24, AddedBy = "seed",
        },
        new()
        {
            Code = "CZ_NUKIB_AKTUALITY", Name = "NÚKIB – aktuality", Publisher = "Národní úřad pro kybernetickou a informační bezpečnost",
            Url = "https://nukib.gov.cz/cs/infoservis/aktuality/", Kind = SourceKind.Official, FetchKind = FetchKind.Html, IsOfficial = true, RefreshHours = 24, AddedBy = "seed",
        },
        new()
        {
            Code = "SK_CERT_AKTUALITY", Name = "SK-CERT – aktuality", Publisher = "Národné centrum kybernetickej bezpečnosti SK-CERT",
            Url = "https://www.sk-cert.sk/sk/aktuality/", Kind = SourceKind.Official, FetchKind = FetchKind.Html, IsOfficial = true, RefreshHours = 24, AddedBy = "seed",
        },
        new()
        {
            Code = "CZ_MVCR_CTHH", Name = "Centrum proti hybridním hrozbám", Publisher = "Ministerstvo vnitra ČR",
            Url = "https://www.mvcr.cz/cthh/", Kind = SourceKind.Official, FetchKind = FetchKind.Html, IsOfficial = true, RefreshHours = 24 * 7, AddedBy = "seed",
        },
        new()
        {
            Code = "EEAS_PRESS", Name = "EEAS press material", Publisher = "European External Action Service",
            Url = "https://www.eeas.europa.eu/eeas/press-material_en", Kind = SourceKind.Official, FetchKind = FetchKind.Html, IsOfficial = true, RefreshHours = 24, AddedBy = "seed",
        },
        new()
        {
            // The Council's site answers a program with a browser check (HTTP 403, 2026-10-21);
            // new sanctions acts reach the workspace through FR_GELS and EU_FSF imports and
            // the regime seed instead. Kept here so the inbox can take hand-pasted items.
            Code = "EU_COUNCIL_PRESS", Name = "Rada EÚ – tlačové správy", Publisher = "Council of the European Union",
            Url = "https://www.consilium.europa.eu/en/press/press-releases/", Kind = SourceKind.Official, FetchKind = FetchKind.Manual, IsOfficial = true, RefreshHours = 24, AddedBy = "seed",
        },
    ];

    public async Task<(int Added, int Existing)> SeedAsync(CancellationToken ct)
    {
        var codes = Defaults.Select(d => d.Code).ToList();
        var existing = await db.Sources.Where(s => codes.Contains(s.Code)).Select(s => s.Code).ToListAsync(ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var added = 0;

        foreach (var template in Defaults.Where(d => !existing.Contains(d.Code)))
        {
            db.Sources.Add(new Source
            {
                Id = Guid.CreateVersion7(),
                Code = template.Code,
                Name = template.Name,
                Publisher = template.Publisher,
                Url = template.Url,
                Kind = template.Kind,
                FetchKind = template.FetchKind,
                IsOfficial = template.IsOfficial,
                Licence = template.Licence,
                RefreshHours = template.RefreshHours,
                IsEnabled = true,
                AddedBy = template.AddedBy,
                CreatedAt = now,
            });
            added++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (added, existing.Count);
    }
}
