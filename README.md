# SancMeld

**Sanctions list aggregation and screening for the EU.**

SancMeld downloads EU, UN and national sanctions lists every day, normalises them into one
queryable model, and screens names against them — with an audit trail that lets you
reconstruct any past decision.

Built for the EU: EU and UN designations are treated as legally binding, third-country
lists such as OFAC are surfaced as red flags only.

```
Status: early development. Seven connectors, working endpoints, no production sign-off.
```

Three properties drive the design:

- **Nothing is ever deleted.** Designations are versioned, so "was this person listed on
  12 March last year?" is answerable.
- **A truncated download cannot empty your list.** Every import passes sanity thresholds
  before anything is published.
- **Recall beats precision.** A missed match is a legal breach; a false positive costs
  analyst time.

---

## Sources

Every endpoint was verified with a live request. Formats and parsing traps are documented
in the connector that implements each one.

| Source | Effect in EU | Format | Records | Status |
|---|---|---|---:|---|
| **EU** Consolidated Financial Sanctions (FSF) | binding | XML | 6 234 subjects | **implemented** |
| **UN** Security Council Consolidated List | binding | XML | 736 persons + 275 entities | **implemented** |
| **FR** Registre national des gels (DG Trésor) | advisory¹ | **JSON API** | 6 578 records | **implemented** |
| **PL** Lista osób i podmiotów (MSWiA) | advisory¹ | XLSX | 445 persons + 139 entities | **implemented** |
| **NL** Nationale sanctielijst terrorisme (BZ) | advisory¹ | ODS | 117 entries | **implemented** |
| **CZ** Vnitrostátní sankční seznam (MZV) | advisory¹ | CSV | 26 entries | **implemented** |
| **OFAC** SDN + Consolidated | **advisory only** | CSV | 19 178 + 481 | planned |
| **UK** UK Sanctions List (FCDO; the OFSI Consolidated List closed 2026-01-28) | **advisory only** | XML | 6 408 designations | **implemented** |

¹ A national list binds in the member state that publishes it, not on a reader elsewhere in
the EU — so it is shown as something to look at, not something that obliges. "Binding in one
member state" is a distinction the model does not have yet. France is imported for the
qualifiers it records twice: 2 051 of its records name the EU designation they correspond
to and 337 the UN one. Poland is imported for the subjects it lists and the EU does not —
Polish subsidiaries of Russian groups, Belarusian officials — and for two measures no
other source has, exclusion from public procurement and an entry ban; its list is prose
in a workbook, with the delisted rows kept and dated, and the connector reads it as such.
The Czech Republic lists by government resolution, mostly people the EU has not listed
yet, and removes them once the EU does — each entry cites the EU act whose criteria the
subject met, so a Czech listing lands under the same programme code as its EU
counterparts. The Netherlands freezes the assets of residents connected with terrorism
under its own regulation implementing UN Resolution 1373 — people the EU's terrorism list
does not carry — and publishes the list as a spreadsheet with no status column, so a
withdrawn entry simply leaves it. The United Kingdom is the first third-country list: a UK
designation obliges nobody in the EU and is surfaced as a red flag, imported for the 5 360
designations that are the UK's own and for the UN references its transpositions carry.

Most EU member states publish no separate national list and rely on EU/UN designations.
That is recorded explicitly per jurisdiction rather than left as an unexplained gap.

### The statement of reasons, searched

The EU listed the three Schneiders on 2026-07-23 and not Svetofor, Torgservice or MERE,
the chain their group runs across Europe; its statement of reasons says they are
"co-founder and co-owner of the Svetofor Group, which runs over 2 200 discount stores …
under brands such as Svetofor, Mere, and MyPrice". A check of the company's name had to
reach that sentence or reach nothing, and until 2026-10-16 it reached nothing: the EU
export carries the statement under each `nameAlias` as a `remark`, and the importer had
been dropping it. It is now the listing's `reason` (1 691 EU subjects carry one; before
2026 mostly a line like "Reportedly defunct", from 2026 the full justification), beside
the UK's `UKStatementofReasons` and France's *motifs*, which were already kept.

Every current listing's free text — reason, function, title, remarks — is indexed as
words (PostgreSQL full-text search under the `simple` configuration, so a company name
reads the same in English, French and Cyrillic text, and nothing is stemmed). A check
whose name occurs in a listing's text gets that listing as a **mention** beside the
bands, with the passage and the field it came from; `GET /designations/mentions?q=` is
the same search on its own. Every word of the query must occur, so "Hung Trade" does not
return every listing that mentions trade. A mention is not a hit: the company is on no
list, and whether the listed person owns or controls it is what the statement says and
the list does not decide.

### Related parties, from Wikidata

A sanction reaches the companies a listed person owns or controls, and no list names
them. Wikidata (CC0) records who owns, founded, chairs and runs what, with a source cited
on many statements, and the `related-parties` job reads it for the persons the lists
already hold. A person is looked up by their **exact date of birth** — the one qualifier
that makes a name match on an open register defensible — and the humans born that day
are compared, every listed spelling against every label in every language, with the same
trigram similarity the screening runs on. Then the item's statements become
`designation_relation` rows: the party, the kind of tie from the person's side (owner of,
founder of, chairs…), the period, the share, and whatever the register cites.

Measured on 2026-10-16 over a random 120 of the 4 270 birth dates on the lists: 140 of
306 persons with a day-precise date resolve (46 %; UK 56 %, EU 48 %, FR 45 %), and 16 of
the 56 distinct items they resolve to carry at least one ownership or control statement.
The query service answers in two to eight seconds a date, so the first fill is a matter
of hours and is run by hand (`related-parties sync`); the nightly job caps itself at
`MaxDatesPerScheduledRun` and drains the rest.

**Why it matters.** EU sanctions regulations freeze not only the funds of the persons
they list but "all funds and economic resources belonging to, owned, held or controlled
by" them (Regulation 269/2014, Article 2(1), and its counterparts). The Commission's
guidance reads *owned* as more than 50 % of the proprietary rights or a majority interest,
and *controlled* as the power to appoint management, to use the assets, or otherwise to
decide — so a company absent from every list can still be one whose assets are frozen,
and the obliged entity is the one who has to notice. OFAC's 50 % rule is the same idea on
the other side of the Atlantic. The lists do not name these companies. Registers do, and
of the open ones Wikidata is the only one that is both free of licence terms and rich in
the one qualifier the match needs.

**What it decides, and what it leaves to the analyst.** It proposes the chain — listed
person, register item, party — with the evidence for each link. It does not settle the
50 % question: the register records a share on few statements (`share`, where present),
never aggregates indirect holdings, and says nothing about control. Reading the result:
open the register record and the sources it cites, satisfy yourself that the register's
person is the listed one (the matched spellings, the similarity and the agreeing date of
birth are shown), then read the kind of tie, the period and the share against your own
records. `external_identity.status` and `designation_relation.status` hold the verdict —
`confirmed` or `rejected` — and a rejected link is never shown again and never remade; an
endpoint for recording the verdict is the next step, and until then it is set in SQL.
Nothing personal is kept that the match did not need: the register's names, ids and
statements, not its biographies.

What this is not: a listing. A check whose name resembles a related party gets the party
**beside** the bands, never in them, with both ends of the chain shown — what the register
says about the tie and why the register's person is taken to be the listed one — as a
candidate until an analyst confirms it, and advisory whichever list the owner is on.
Anyone can edit Wikidata; that is why the evidence travels with every link, and why a
person without a day-precise date of birth is not looked up at all.

**When a listing arrives, and the picture.** The import hands the listings it just wrote
to every register connector at once (`IRelatedPartyEnricher`), so the morning a person
is listed, the question "what else does this touch" has its answer; a register that is
down costs a warning, never the import. `GET /designations/{id}/graph` draws the answer:
the other lists naming the same subject (cross-references, a publisher's own word), the
parties a register ties them to and whoever else is tied to those parties (co-owners,
co-founders), and the statements of reasons that mention them or that theirs names.
Every edge carries one of four statuses and its evidence — **confirmed** (a publisher's
or an analyst's word), **candidate** (a machine's, with the match and the source cited),
**review** (a candidate that reaches a binding listing or a controlling share, the one a
person looks at today), **rejected** (kept, greyed, so the reader sees it was
considered) — and the front end colours by status, never by kind. `GET /designations/recent`
is the same picture as a feed: the last runs that added or changed listings, each new
subject with the counts and the first lines of what it touches, and the worst status
among them. The graph decides nothing; it puts in one picture what four queries would
have shown.

**Running it.** `SancMeld.Server related-parties sync` looks up every pending person and
refreshes the relations of every resolved one, committing as it goes; `--max-dates N`
bounds a run, and `related-parties status` prints how far coverage has got. The first
fill is one query per distinct birth date — hours, at the half-second pause the query
service's policy asks for — so run it once by hand, detached, and let the nightly job
(`SancMeld:Wikidata:CronExpression`, off when the scheduler is off) keep up with new
listings and re-read relations every `RefreshRelationsAfterDays`. A person the register
did not have is tried again after `RetryMissAfterDays`. `SancMeld:Wikidata:Enabled=false`
stops every query and job; what was stored keeps serving. The client sends the
`SancMeld:Ingest:UserAgent`, runs one query at a time and honours Retry-After, which is
what the Wikidata Query Service asks of a bot.

### Beneficial owners, from the Slovak RPVS

The register of public sector partners (RPVS, Ministry of Justice of the Slovak
Republic) lists the beneficial owners of every company that contracts with the Slovak
state, each a natural person with a full date of birth on the newer entries, as open
data. `rpvs sync` reads the whole of it — about sixty thousand partners, three thousand
OData pages, some ten minutes — and compares every owner here: an owner born on a day
no listed person was born is passed over without a query, the rest go through the same
birth-date-then-name match ARES and Wikidata use. Nothing a user screens is sent to
RPVS, which is the difference from the ARES lookup, and only the owners who turn out to
be listed are stored, as candidate `OwnerOf` links from the listed person to the
company, with the partner's public RPVS page as the evidence.

```bash
dotnet run --project src/SancMeld.Server -- rpvs sync [--max-pages N]
```

A weekly job (`SancMeld:Rpvs:CronExpression`) keeps it current; a full pass marks the
links it no longer saw as not current, a partial one leaves them alone. The register
covers only partners of the public sector, so a Slovak company that never dealt with
the state is not in it. Other Slovak registers come in only where they give a date of
birth: a name alone is not enough to link anyone.

---

## Quick start

Needs Docker, the .NET 10 SDK, and PostgreSQL 16+ with `pg_trgm`, `unaccent`,
`fuzzystrmatch`, `pgcrypto` and `btree_gin` (the compose file brings one).

```bash
git clone https://github.com/MatoPopper/SancMeld
cd SancMeld

docker compose up -d db
dotnet tool install --global dotnet-ef
dotnet ef database update --project src/SancMeld.Core

dotnet run --project src/SancMeld.Server -- import EU_FSF   # ~25 MB, ~6 200 subjects
dotnet run --project src/SancMeld.Server                    # /swagger, /jobs
```

Later imports cost 4 kB when the Commission has published nothing new: the connector
compares the version its RSS feed advertises before downloading anything.

EF Core migrations are the only way a SancMeld schema comes into existence — there is no
second, hand-maintained SQL file to drift away from them. Point them at another database
with `SANCMELD_DESIGN_CONNECTION`.

One recurring import job per source, driven by the `sanctions_source` rows rather than a
list in code, so adding a connector never means editing the host. The scheduler is thirty
obvious lines of Hangfire in `SancMeld.Server`; the job itself is the same call that
`-- import EU_FSF` makes. Turn it off with `SancMeld:Jobs:Enabled=false`.

---

## API

| | |
|---|---|
| `GET /designations` · `/designations/{id}` | every current listing, cursor-paged |
| `GET /designations/search?q=` | trigram search over every recorded spelling |
| `GET /designations/mentions?q=` | listings whose statement of reasons mentions the words |
| `GET /designations/{id}/related` | what open registers say the subject owns, founded or runs |
| `GET /designations/{id}/graph` | the listing and everything it is tied to, as nodes and edges with a status and evidence each |
| `GET /designations/recent` | what the last import runs added, and what each new listing touches |
| `POST /check` | a name plus whatever is known, answered as a decision — **writes** |
| `POST /screen` | the same evidence as scores and feature vectors — **writes** |
| `POST /adjudications` | what the analyst concluded — **writes** |
| `GET /adjudications/reasons` · `/training/samples` | the coded reasons, and the labelled corpus |
| `GET /auth/me` · `/admins` … | who may administer this instance, when a sign-in is configured |

Reads stay open; the endpoints marked **writes** require a sign-in where one is configured
(see [Signing in](#signing-in)). They write because a compliance decision that cannot be
reconstructed is not one anyone can stand behind: the query, every candidate considered and
the evidence behind each score are all stored.

Nothing is filtered out for being sparse. Of the 6 234 subjects on the EU list, 2 364 carry
no date of birth and 4 245 no identifier at all — an incomplete listing is not a lesser
listing, and the subject is sanctioned either way.

That is why each qualifier is answered separately rather than folded into one number:

```jsonc
{ "score": 0.525, "aboveThreshold": true,
  "explanation": {
    "nameScore": 0.875,
    "identifierMatch": false,
    "birthYear": "Contradicts",       // agrees · contradicts · unknownOnRecord · notProvided
    "country": "NotProvided",
    "notes": ["None of the 3 recorded years of birth matches the one supplied."] } }
```

`UnknownOnRecord` and `NotProvided` both score zero. Treating "the list does not say" as
"the list disagrees" would push exactly the subjects nobody knows much about below the
threshold.

---

## Matching, and where ML.NET fits

Matching runs in layers, cheapest and most certain first:

1. **Deterministic** — passport, national ID, IMO, LEI, and publisher-declared
   cross-references (`REFERENCE_UE`, `REFERENCE_ONU`)
2. **Candidate generation** — `pg_trgm`, deliberately wide, tuned for recall
3. **Hard filters** — subject-type mismatch, surname-only match
4. **Scoring and ranking** — rules today, behind `IScoringStrategy`
5. **Analyst adjudication** — captured as training data

Step 2 is why PostgreSQL is not interchangeable here. Sanctions matching is
character-level, not word-level: 19 % of genuinely similar name pairs on the EU list share
no exact token, so a tokenised full-text index structurally cannot reach them. Measured on
real data, `pg_trgm` reaches 100 % recall@10 where MariaDB `FULLTEXT` reaches 84.5 %.

**ML.NET is planned for step 4 only, and that is a result, not a preference.** A LightGBM
ranker over name pairs was built and measured against the plain trigram index — 200 queries
against every recorded spelling:

| Ranking | recall@1 | recall@10 | MRR |
|---|---:|---:|---:|
| `pg_trgm` alone | **68.0 %** | **100.0 %** | **0.824** |
| ML.NET LightGBM | 36.0 % | 87.0 % | 0.551 |

The model lost, and it also *lost matches the bare index finds*. An earlier number said the
opposite — AUC-PR 0.939 against 0.230 — and was invalid: hard negatives had been generated
as pairs with trigram similarity ≥ 0.45, so the evaluation set was built out of exactly the
pairs the trigram cannot separate. It measured the construction, not the index.

So names stay with the trigram, and a model is aimed at **weighting the qualifiers** —
how much a matching birth year, country or document should move a score. That arrives as a
second `IScoringStrategy` and a second row in `match_algorithm`, never as an edit to the one
past decisions point at, because a stored decision must keep meaning what it meant.

**One thing a model does reach that the trigram cannot: another script.** A multilingual
embedding model (`bge-m3`) was measured as a *second* candidate generator beside the
trigram, never instead of it — `tools/SancMeld.Experiments.NameEmbeddings` has the method
and the numbers. On 7 981 listings whose non-Latin spelling was hidden from the index and
used as the query, the trigram reaches 0 % by construction and the embedding 96.3 %
recall@25 (Cyrillic 98.9 %, Arabic 88.6 %, Korean 61.7 %). On the 2 411 pairs publishers
declare between their lists, fusing the two adds 0.6 points and loses nothing the trigram
had. The cost is one more vector per recorded name: 8 ms a query by brute force, and 45–76
ms to embed the query on a 2012 Xeon with no GPU.

It is built as far as a **shadow**: with `SancMeld:Embeddings:Mode=Shadow` the import
embeds every recorded name (`designation_name_embedding`, the model as a row in
`embedding_model`), each screening retrieves through the model as well, and what only the
model reached is recorded in `screening_candidate` with `retrieved_by = semantic` — below
everything shown, never surfaced, scores untouched. The shadow period answers, on real
queries rather than publishers' spellings, what the embedding would have added; the
scorer learns to read `semantic_score` only after it has.

The vectors are searched from memory, by brute force, and the copy follows the table
rather than the process: before each screening the API compares two timestamps (the
newest vector, the last successful import — 0.2 ms off an index) and reloads when the
table has moved on and been quiet for `IndexSettleSeconds`. An import run through
`docker exec` in another container is picked up by the next screening; no restart.

The labels cannot come from string similarity: 56.3 % of genuinely positive pairs are not
string-related at all — different script (`Michail GRIB` ↔ `Міхаіл ГРЫБ`), translation
(`JSC FINAM Investment Bank` ↔ `АО Банк ФИНАМ`), or an unrelated alias. They come from
analysts through `/adjudications`, and from publishers' own cross-references: of 86 links
the EU list carries into the UN list, 83 resolve, and they pair spellings no string
comparison would ever match — `Ch'o'l Cho` ↔ `CHO IL U`.

Status: proof of concept. Nothing is trained in this repository, `/training/samples` hands
the corpus out for training elsewhere, and a model that could be retrained by an HTTP call
is one nobody can audit. ML.NET 5.0.0 is stable; `AutoML` is still 0.23.0 and stays out of
the production path.

---

## Signing in

Off by default: with no client id configured the host is entirely unauthenticated — what
the open-source edition is for — and `/auth` and `/admins` are not mapped at all.

Turning it on guards the endpoints that write, and the endpoints that hand out access.
Reading stays open: it is a published sanctions list, and a login in front of it would
protect nothing that is not already on the publishers' own websites. What a login protects
is this database, where every screening leaves rows attributed to somebody.

```bash
# an OAuth client id from Google Cloud Console (Web application)
export SancMeld__Auth__GoogleClientId=1234-abcd.apps.googleusercontent.com

# the first entry on the allow list, from a shell — /admins requires being on it already
dotnet run --project src/SancMeld.Server -- admin add you@example.com --name "Your Name"
dotnet run --project src/SancMeld.Server -- admin list [--all]
dotnet run --project src/SancMeld.Server -- admin revoke someone@example.com
```

**There is no client secret, here or anywhere else.** The API is a resource server: it
verifies Google's ID token against Google's published signing keys and never calls Google,
so no secret exists that could leak. Obtaining the token is the client's job — open
`/login.html` to get one before a front end exists.

Two questions are kept apart, and `GET /auth/me` answers both. *Who are you* is settled by
Google, for anybody in the world with an account. *Are you anyone here* is settled by the
`admin` table, checked on every request rather than baked into the token, so revoking takes
effect at once where an ID token stays valid for an hour. Attribution follows the same
rule: `requestedBy` and `decidedBy` exist for an unauthenticated host, but where a token is
present it wins — a self-declared identity beside a proven one is just a way to file work
under a colleague's name.

**Who may screen, and how much.** Since 2026-10-16 the `admin` table only decides who
manages the allow list. Screening — `POST /check`, `POST /screen`, `POST
/registers/ares/lookup` — is open to anybody whose Google address is verified, within a
budget kept in `api_user`: an address seen for the first time gets
`SancMeld:Auth:DefaultRequestLimit` requests (50) in total, not per day, and every metered
request spends one in a single conditional UPDATE, so two requests at once cannot both
spend the last. An exhausted budget answers `429`, and the front end then offers a short
form — why they need access and the industry they work in — that `POST /quota-requests`
stores in `quota_request` beside the caller's verified address, one open request per
address. `GET /auth/me` reports the limit, what is left, and whether a request is waiting. There
is no screen for raising a budget, on purpose — nobody can raise their own:

```sql
UPDATE api_user SET request_limit = 1000 WHERE email = 'someone@example.com';
```

The migration that introduced the table gave everyone then on the allow list a budget of
a million.

> **Google requires an `https` origin**, with `http://localhost` the single exception. A
> deployment on plain http can accept tokens, but no browser on it can produce one.

---

## Configuration

`appsettings.json` ships pointing at the development container in `docker-compose.yml`.
That connection string is deliberately throwaway — user `sancmeld`, password `sancmeld`,
port 55432, every value already published in the compose file. It is a convenience, not a
credential, and **it must never be edited to hold a real one**: a production password
reaching a tracked file is in the history for good, and this project's predecessor shipped
exactly that.

Override it from outside the repository instead — each of these wins over
`appsettings.json`, so nothing has to be deleted first:

```bash
dotnet user-secrets set "ConnectionStrings:SancMeld" "Host=…" --project src/SancMeld.Server
export ConnectionStrings__SancMeld="Host=db.example.com;Database=sancmeld;Username=app;Password=…"
# or a git-ignored src/SancMeld.Server/appsettings.Development.json
```

Port 55432 rather than 5432 is deliberate: anyone already running PostgreSQL locally would
otherwise have their own server answer on `localhost:5432` and see an authentication
failure that looks like a SancMeld bug.

| Setting | Default | Notes |
|---|---|---|
| `SancMeld:Ingest:UserAgent` | `SancMeld/1.0 …` | Required — OFAC returns 403 without one |
| `SancMeld:Ingest:SnapshotStoragePath` | `./.snapshots` | Raw downloads, git-ignored |
| `SancMeld:Matching:SimilarityThreshold` | `0.35` | Lower = wider net = higher recall |
| `SancMeld:Matching:ScoreWeakAliases` | `false` | Generic aliases corroborate, they do not raise hits |
| `SancMeld:Auth:GoogleClientId` | *empty* | Empty = no sign-in. A public value, not a secret |
| `SancMeld:Embeddings:Mode` | `Off` | `Shadow` retrieves through the embedding model and records, never shows |
| `SancMeld:Embeddings:Endpoint` | `http://localhost:11434` | An Ollama-compatible `/api/embed` |
| `SancMeld:Embeddings:ModelCode` | `BGE_M3` | A row in `embedding_model`; the row says which model file and how many dimensions |
| `SancMeld:Embeddings:IndexSettleSeconds` | `10` | The in-memory index reloads once the vector table has been quiet this long; a running backfill writes every second or two |
| `SancMeld:Wikidata:Enabled` | `true` | Read Wikidata for what listed persons own and run; off means no query and no job, the stored links still serve |
| `SancMeld:Wikidata:CronExpression` | `30 2 * * *` | The nightly run, capped at `MaxDatesPerScheduledRun` (300) birth dates |
| `SancMeld:Wikidata:MinNameScore` | `0.6` | Trigram similarity a listed spelling must reach against a register label, once the birth date agrees |
| `SancMeld:Rpvs:Enabled` | `true` | Read the Slovak RPVS for beneficial owners; off means no call and no job |
| `SancMeld:Rpvs:CronExpression` | `30 4 * * 0` | The weekly full pass |
| `SancMeld:Rpvs:DelayMilliseconds` | `100` | Pause between the three thousand pages |
| `SancMeld:Jobs:Enabled` | `true` | Off means no Hangfire and no `/jobs` |
| `SancMeld:Database:AutoMigrate` | `false` | Migrate on start; for single-container deployments |

Sanctions sources live in the `sanctions_source` table, not in configuration, so imports
and snapshots can reference them by key.

---

## How it works

```
DiscoverEndpoint → Download → ValidateTransport → StoreSnapshot
      → Parse → ValidateDataset → Stage → Diff → Commit → ResolveEntities → Audit
```

Endpoint discovery is a separate step from download because government attachment URLs
change: Poland ships a GUID attachment, Czechia a dated PDF filename.

```
src/
  SancMeld.Core/            domain, data access, matching, ingest pipeline
    Data/                   DbContext and EF Core migrations — the schema of record
    Ingest/                 pipeline phases and safety guards
    Matching/               normalisation, string metrics, scoring
    Access/                 the admin allow list
  SancMeld.Connectors.*/    one project per source
  SancMeld.Api/             endpoints as a LIBRARY: AddSancMeld() / MapSancMeld()
  SancMeld.Server/          thin host
tests/                      unit tests, plus integration tests against a real PostgreSQL
tools/                      measurements that decide what gets built — not shipped
fixtures/                   real downloaded samples, for parser regression tests
```

`SancMeld.Api` is a library, not an application. That is what lets a closed-source host
reuse the core without forking it.

---

## Deploying

One host, which builds the image itself:

```bash
./deploy.sh             # ships HEAD — not the working tree — builds on the host, starts, checks /health
./deploy.sh rollback    # the previous image back, one command
```

`git archive HEAD` goes over SSH to `~/sancmeld/app`, the multi-stage Dockerfile builds
there, `docker compose up -d` restarts the container, and the script refuses to call it
done until `/health` answers through the host's port and then through the public URL. The
image that was running is kept as `:previous`; the new one carries the commit as an OCI
label, so `docker image inspect` says what is deployed.

The script names no host. Copy `deploy.env.example` to `deploy.env` (git-ignored) with
your SSH target and public URL — an SSH user beside a host name is half a login, and this
repository is public. No secrets pass through either: the connection string and the Google
client id live in `docker-compose.yml` on the host, where migrations are applied on start
(`SancMeld:Database:AutoMigrate=true`).

## Open core

This repository is the complete, usable core under Apache-2.0: all connectors, the schema,
the matching engine and the screening API.

A separate closed-source edition adds hosting concerns — multi-tenancy, billing, quotas and
models trained on aggregated adjudications. It consumes this repository as versioned NuGet
packages and never patches it. If it ever needs to, that means the core is missing an
extension point, and the extension point belongs here.

---

## Contributing

New connectors are the most valuable contribution, especially national lists not yet
covered. See [CONTRIBUTING.md](CONTRIBUTING.md). Contributions are accepted under the
[DCO](https://developercertificate.org/): sign your commits with `git commit -s`.

---

## Disclaimer

SancMeld is a tool, not legal or compliance advice. The consolidated files it downloads are
operational aids; the legally authoritative text is the Official Journal of the EU and the
equivalent national publications. Verify designations against the official source before
acting on them, and obtain your own compliance sign-off before relying on this software.

---

## License

[Apache-2.0](LICENSE)
