# HybridMeld

**A workspace on hybrid threats for analysts and journalists, beside the sanctions and
PEP screening.**

HybridMeld lives in this repository as its own set of projects (`src/HybridMeld.*`,
`tests/HybridMeld.*`) and is deployed as its own container against its own database. It
reuses SancMeld.Core by project reference for name normalisation, the enum converter and
the graph records the front end draws, and it *reads* the sanctions database for three
things: the grant on the account, the subjects of the EU hybrid-threats and cyber
regimes it seeds itself from, and the targets of the links an analyst sets. It writes to
no table of the other two services.

```
Status: first increment. Cases, actors, claims with quoted evidence and kept snapshots,
decisions with an audit trail, watched sources (RSS/Atom, public Telegram channels, web
pages) with an inbox, the graph in the sanctions graph's shape, the seed from the EU
regimes RUSDA and CYB. Front end: the /hybrid mode of SancMeldFE.
```

## What it is, and is not

It is **not** a screening and follows no AML law: nothing obliges anyone to use it, and
its readers are a small circle of analysts and journalists who weigh sources themselves.
It is a desk for cases: a case (campaign) is the principal entity, actors are tied to it
through claims, and every claim rests on at least one verbatim quote from a source whose
page is kept as a snapshot. Unverified sources are welcome; the level on each claim says
how far to trust what came out of them, as a label and never as a gate.

The reason it sits in this solution is the overlap: the EU sanctions list already carries
a regime for Russia's destabilising activities (Council Regulation (EU) 2024/2642,
programme `RUSDA`) and a cyber regime (2019/796, `CYB`), and a case reaches sanctioned
subjects and, now and then, a politically exposed person. The links go **one way**: from an
actor here to a sanctions record (`/designations/{id}`) or a PEP person. No sanctions or
PEP screen knows the workspace exists, and a compliance user never sees it.

## Model

| Entity | What it is |
|---|---|
| `campaign` | A case: title, summary, kind (FIMI, cyber, sabotage, election interference, espionage, proxy influence, economic coercion, migration instrumentalisation), status, countries, editorial state (Draft, Published, Withdrawn with a reason). |
| `actor` | A person, organisation, website, channel, military unit or state body; one row per real-world thing across every case. Soft links `sanction_designation_id` and `pep_person_id` with cached names, verified by a job, marked stale when the target is gone. A private person is named on the curator's responsibility, with the curator's name on the row. |
| `actor_alias` | Other names, with where they come from (analyst, sanctions, misp, mitre). |
| `claim` | One sentence: subject, predicate (operates, controls, funds, amplifies, defends, appears on, member of, attributed to, listed under…), optional object; level A–D; status Candidate, Confirmed, Rejected, Withdrawn; found by analyst, agent or official act. |
| `evidence` | URL, publisher, verbatim quote (mandatory), publication date, snapshot path and hash, archive URL. |
| `claim_decision` | Every decision taken on a claim, in order. |
| `source`, `source_run`, `source_item` | Watched sources, their fetches, and the inbox of what they brought, with a snapshot of each item. |

Levels: **A** an official act (a regulation with its reasons, a ruling, a national security
authority's report); **B** a state-established body or a major platform, or investigative
journalism with documents; **C** media or a think-tank without documents, a name match
without a date of birth; **D** indirect evidence, an inference. *Confirmed* means a person
checked that the quote is in the source and the sentence does not go beyond it, not that
the claim is true.

## Access

One flag on the SancMeld account, `is_allowed_for_hybrid`, set on SancMeld's users screen
or with `users hybrid <email> on` of SancMeld.Server. HybridMeld reads it on every request
through `ConnectionStrings:SancMeldAccounts` and refuses an address without it; with no
such connection configured (a development host) every verified address is let in. Everyone
with the flag reads, adds watched sources, records claims and decides on them; who did
what is on every row. API keys exist (`apikey create`) but none is issued: the agent is not
connected yet.

## Running

```
docker compose up -d db
dotnet ef database update --project src/HybridMeld.Core --startup-project src/HybridMeld.Server
dotnet run --project src/HybridMeld.Server -- hybrid seed-regimes        # RUSDA + CYB from the sanctions database
dotnet run --project src/HybridMeld.Server                                # Swagger at /swagger
```

CLI: `hybrid seed-regimes [RUSDA CYB]`, `hybrid sources seed` (the official feeds and
pages checked on 2026-10-21: EUvsDisinfo, UK FCDO, UK NCSC, NÚKIB, SK-CERT, MV ČR CTHH,
EEAS; the Council's press site refuses programs and is Manual), `hybrid sources fetch
<CODE>|--due`, `hybrid aliases sync` (the MISP threat-actor galaxy: a cluster whose name
is already on an actor, as the EU list's "Sandworm Team" is on GRU unit 74455, lends it
its other names, "Seashell Blizzard", "APT44"; only names not from the galaxy count as
the anchor, so shared terms like "Grizzly Steppe" never chain APT29 onto a GRU unit),
`hybrid import-case <file.json> [--by email]` (a curated case from a file: actors matched
to existing ones by exact name or alias, so a sanctioned subject keeps its link; claims
matched by case, subject, predicate, object and statement, so a re-import adds only what
is new; each claim goes through the same rule and the same page snapshot as one typed in
the screen), `hybrid links verify [--all]`, `hybrid actors forget <id>` (the one deletion: a person asks
to be removed; their claims go, the decisions stay without the name), `apikey …`, `users …`.

The scheduler fetches due sources every fifteen minutes (a Telegram channel defaults to
hourly, a feed to daily), verifies links daily and reads the galaxy weekly. `HybridMeld__Jobs__Enabled=false` turns
it off.

## API

`/campaigns` (list through the filter bar: since, countries, kinds, touchesSanctions,
touchesPep, minLevel, confirmedOnly, includeSeed, includeDrafts, includeClosed, q;
`/facets` with the counts each chip would show; create, `{slug}`, `{slug}/graph`,
`{slug}/editorial`), `/actors`
(`?q=` search over names and aliases, `{id}`, `{id}/graph`, `{id}/links`), `/claims`
(`{id}`, `{id}/evidence`, `{id}/decision`), `/feed?since=`, `/sources`
(`{code}`, `{code}/fetch`), `/inbox` (`{id}`, `{id}/triage`). The graph endpoints answer
the same `DesignationGraph` record the sanctions API does, so the front end draws both
with one component.

## Container

`Dockerfile.hybridmeld`, built from the repository root. Environment:
`ConnectionStrings__HybridMeld`, `ConnectionStrings__SancMeldAccounts`,
`ConnectionStrings__PepMeld` (optional), `HybridMeld__Auth__GoogleClientId`,
`HybridMeld__Cors__AllowedOrigins__0`. Snapshots live under
`/var/lib/hybridmeld/snapshots`; mount a volume there.
