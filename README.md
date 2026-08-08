# SancMeld

**Sanctions list aggregation and screening for the EU.**

SancMeld downloads EU, UN and national sanctions lists every day, normalises them into
one queryable model, and screens names against them — with an audit trail that lets you
reconstruct any past decision.

Built for the EU: EU and UN designations are treated as legally binding, third-country
lists such as OFAC are surfaced as red flags only.

```
Status: early development. The schema and connectors are being built.
Not yet suitable for production compliance use.
```

---

## Why

Most sanctions-screening tooling is either a closed SaaS black box or a scraper that
silently breaks when a government changes a URL. SancMeld tries to be the third thing:
an open, auditable pipeline where you can see exactly which file was downloaded, when,
what its checksum was, and how a name ended up matching.

Three properties drive the design:

- **Nothing is ever deleted.** Designations are versioned, so "was this person listed on
  12 March last year?" is answerable.
- **A truncated download cannot empty your list.** Every import passes sanity thresholds
  before anything is published.
- **Recall beats precision.** A missed match is a legal breach; a false positive costs
  analyst time. The system is tuned accordingly.

---

## Sources

Every endpoint below was verified with a live request. Formats, quirks and parsing traps
for each source are documented in the connector project that implements it.

| Source | Effect in EU | Format | Records | Status |
|---|---|---|---:|---|
| **EU** Consolidated Financial Sanctions (FSF) | binding | XML | 6 234 subjects | **implemented** |
| **UN** Security Council Consolidated List | binding | XML | 736 persons + 275 entities | **implemented** |
| **FR** Registre national des gels (DG Trésor) | binding | **JSON API** | 6 574 records | planned |
| **PL** Lista osób i podmiotów (MSWiA) | binding | XLSX | ~738 rows | planned |
| **NL** Nationale terrorismelijst | binding | ODS | small | planned |
| **CZ** Vnitrostátní sankční seznam (MZV) | binding | PDF | small | research |
| **OFAC** SDN + Consolidated | **advisory only** | CSV | 19 178 + 481 | planned |
| **UK** OFSI Consolidated List | **advisory only** | CSV / XML | — | candidate |

Most EU member states publish no separate national list and rely on EU/UN designations.
That is recorded explicitly per jurisdiction rather than left as an unexplained gap.

---

## Quick start

Requires Docker and the .NET 10 SDK.

```bash
git clone https://github.com/MatoPopper/SancMeld
cd SancMeld

# PostgreSQL with the extensions SancMeld needs
docker compose up -d db

# create the schema
dotnet tool install --global dotnet-ef
dotnet ef database update --project src/SancMeld.Core

# build, then import the EU list
dotnet build
dotnet run --project src/SancMeld.Server -- import EU_FSF

# or serve the API
dotnet run --project src/SancMeld.Server
```

| | |
|---|---|
| `/swagger` | try the endpoints |
| `/jobs` | scheduled imports (Hangfire), local requests only |

The first import fetches roughly 25 MB and publishes about 6 200 subjects. Later runs cost
4 kB when the Commission has published nothing new, because the connector compares the
version its RSS feed advertises before downloading anything.

EF Core migrations are the only way a SancMeld schema is created — there is no
second, hand-maintained SQL file to drift away from them. To point the migration at
a database other than the one in `docker-compose.yml`, set `SANCMELD_DESIGN_CONNECTION`.

`SancMeld.Server` is single-tenant and unauthenticated by design — do not expose it
directly to the internet. Authentication, multi-tenancy and quotas live in the hosted
edition; see [Open core](#open-core) below.

---

## API

| | |
|---|---|
| `GET /designations` | every current listing, cursor-paged |
| `GET /designations/{id}` | one listing in full |
| `GET /designations/search?q=` | trigram search over every recorded spelling |
| `POST /screen` | a name plus whatever else is known, scored and recorded |

The first three are reads. `POST /screen` also writes: the query, every candidate it
considered and the evidence behind each score are stored, because a compliance decision
that cannot be reconstructed is not one anyone can stand behind.

Nothing is filtered out for being sparse. Of the 6 234 subjects on the EU list, 2 364 carry
no date of birth and 4 245 carry no identifier at all — an incomplete listing is not a
lesser listing, and the subject is sanctioned either way.

That is also why `/screen` answers each qualifier separately rather than folding it into
one number:

```jsonc
{ "score": 0.525, "aboveThreshold": true,
  "explanation": {
    "nameScore": 0.875,
    "identifierMatch": false,
    "birthYear": "Contradicts",       // agrees · contradicts · unknownOnRecord · notProvided
    "country": "NotProvided",
    "subjectType": "NotProvided",
    "notes": ["None of the 3 recorded years of birth matches the one supplied."] } }
```

`UnknownOnRecord` and `NotProvided` both score zero. Treating "the list does not say" as
"the list disagrees" would push exactly the subjects nobody knows much about below the
threshold, and a missed match is a legal breach where a false positive is an hour of work.

Scoring is rule-based today. The weights sit behind `IScoringStrategy`, and every screening
stores the feature vector they were applied to, so a trained model can arrive as a second
implementation and a second row in `match_algorithm` — never as an edit to the one that
past decisions point at.

---

## Scheduled imports

One recurring job per sanctions list, driven by the `sanctions_source` rows rather than by
a list in code: the cron expression sits on the source next to the endpoint it fetches, so
adding a connector never means editing the host.

Hangfire keeps its own tables in a `hangfire` schema alongside the data, and the dashboard
at `/jobs` accepts local requests only — it can trigger and delete jobs, and this host has
no authentication of its own to put in front of it.

The whole scheduler is about thirty lines in `SancMeld.Server`, on purpose. Job storage,
retention and dashboard access are exactly the things a deployment will have opinions
about, and the job itself is one call into `SanctionsImportService` — the same call
`-- import EU_FSF` makes. Replace the host, keep the libraries.

---

## Configuration

> **You need a database before anything runs.** `appsettings.json` ships pointing at the
> development container in `docker-compose.yml`, but the container is not part of the
> build — start it and apply the migrations, or point SancMeld at a PostgreSQL 16+
> instance you already have.
>
> ```bash
> docker compose up -d db
> dotnet ef database update --project src/SancMeld.Core
> ```

The committed connection string is deliberately throwaway — user `sancmeld`, password
`sancmeld`, port 55432 — and every one of those values is already published in
`docker-compose.yml`. It is a convenience, not a credential.

To use your own database, override it from outside the repository. Any of these wins over
`appsettings.json`, so nothing has to be edited or deleted:

```bash
# development — stored outside the repository entirely
dotnet user-secrets set "ConnectionStrings:SancMeld" \
  "Host=db.internal;Database=sancmeld;Username=app;Password=..." \
  --project src/SancMeld.Server

# docker / production
export ConnectionStrings__SancMeld="Host=db.example.com;Database=sancmeld;Username=app;Password=..."
```

Or drop a git-ignored `src/SancMeld.Server/appsettings.Development.json` next to the
committed one, holding nothing but the `ConnectionStrings` section.

Port 55432 rather than 5432 is deliberate: anyone already running PostgreSQL locally would
otherwise have their own server answer on `localhost:5432` and see an authentication
failure that looks like a SancMeld bug.

| Setting | Default | Notes |
|---|---|---|
| `SancMeld:Ingest:UserAgent` | `SancMeld/1.0 …` | Required — OFAC returns 403 without one |
| `SancMeld:Ingest:SnapshotStoragePath` | `./.snapshots` | Raw downloads, git-ignored |
| `SancMeld:Matching:SimilarityThreshold` | `0.35` | Lower = wider net = higher recall |
| `SancMeld:Matching:ScoreWeakAliases` | `false` | Generic aliases corroborate, they do not raise hits |

Sanctions sources are configured in the `sanctions_source` table, not in `appsettings.json`,
so imports and snapshots can reference them by key.

> **Never edit `appsettings.json` to hold a real credential.** A local container's
> throwaway password is one thing; a production password reaching a tracked file is in the
> history for good. The predecessor of this project shipped exactly that. `.gitignore` is set
> up to stop it recurring — use one of the three overrides above.

---

## Requirements

- **PostgreSQL 16+** with `pg_trgm`, `unaccent`, `fuzzystrmatch`, `pgcrypto`, `btree_gin`
- **.NET 10 SDK**

PostgreSQL is not interchangeable here. Sanctions matching is character-level, not
word-level: 19 % of genuinely similar name pairs in the EU list share no exact token,
so a tokenised full-text index structurally cannot find them. Measured on real data,
`pg_trgm` reaches 100 % recall@10 where MariaDB `FULLTEXT` reaches 84.5 %.

---

## How it works

```
DiscoverEndpoint → Download → ValidateTransport → StoreSnapshot
      → Parse → ValidateDataset → Stage → Diff → Commit → ResolveEntities → Audit
```

Endpoint discovery is a separate step from download because government attachment URLs
change: Poland ships a GUID attachment, Czechia a dated PDF filename.

Matching runs in layers, cheapest and most certain first:

1. **Deterministic** — passport, national ID, IMO, LEI, and publisher-declared
   cross-references (`REFERENCE_UE`, `REFERENCE_ONU`)
2. **Candidate generation** — `pg_trgm`, deliberately wide, tuned for recall
3. **Hard filters** — subject-type mismatch, surname-only match
4. **Scoring and ranking** — rules today
5. **Analyst adjudication** — captured as training data

---

## Project layout

```
src/
  SancMeld.Core/            domain, data access, matching, ingest pipeline
    Abstractions/           ISanctionsConnector, ITenantResolver, IScoringStrategy
    Domain/                 entities and enums
    Data/                   DbContext and EF Core migrations — the schema of record
    Ingest/                 pipeline phases and safety guards
    Matching/               normalisation, string metrics, scoring
  SancMeld.Connectors.*/    one project per source
  SancMeld.Api/             endpoints as a LIBRARY: AddSancMeld() / MapSancMeld()
  SancMeld.Server/          thin host, single-tenant, no auth

tests/
  SancMeld.Core.Tests/      unit tests
ml/prototype/               matching experiments, not part of the build
fixtures/                   real downloaded samples, for parser regression tests
```

`SancMeld.Api` is a library, not an application. That is what lets a closed-source host
reuse the core without forking it.

---

## Open core

This repository is the complete, usable core under Apache-2.0: all connectors, the schema,
the matching engine and the screening API.

A separate closed-source edition adds hosting concerns — Google/OIDC authentication,
multi-tenancy, billing, quotas and models trained on aggregated adjudications. It consumes
this repository as versioned NuGet packages and never patches it. If it ever needs to,
that means the core is missing an extension point, and the extension point belongs here.

---

## Contributing

New connectors are the most valuable contribution — especially national lists we have not
covered. See [CONTRIBUTING.md](CONTRIBUTING.md).

Contributions are accepted under the [DCO](https://developercertificate.org/): sign your
commits with `git commit -s`.

---

## Disclaimer

SancMeld is a tool, not legal or compliance advice. The consolidated files it downloads are
operational aids; the legally authoritative text is the Official Journal of the EU and the
equivalent national publications. Verify designations against the official source before
acting on them, and obtain your own compliance sign-off before relying on this software.

---

## License

[Apache-2.0](LICENSE)
