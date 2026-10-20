# PepMeld

**Screening of politically exposed persons (PEP) for Slovakia and Czechia.**

PepMeld lives in this repository as its own set of projects (`src/PepMeld.*`,
`tests/PepMeld.*`) and is deployed as its own container against its own database. It
reuses SancMeld.Core by project reference for what the two have in common, name
normalisation and trigram scoring, and nothing else: no table, no endpoint and no
process is shared.

```
Status: first increment. Four lists (CZ Chamber of Deputies open data, SK National Council
deputies, SK register of public officials, CZ state-company boards from ARES), the
asynchronous check API with stage 1 of three, API-key access with budgets. No front end yet.
```

## What it answers

Given a name **and a date of birth**, is this person in a prominent public function as
defined by the national AML law (SK § 6 zák. č. 297/2008 Z. z., CZ § 4 odst. 5 zák.
č. 253/2008 Sb.), was she in one and how long ago did it end, and how good is the
evidence. The date of birth is mandatory on purpose: most official lists publish none,
and a PEP check without it is a guess among namesakes. Where a source does publish it,
a name match is confirmed (level **A**) or excluded; where it does not, the match stays a
candidate (level **C**) and the finding says what would settle it.

A check runs in three stages. Each writes its findings as soon as it ends, so the caller
sees the lists within seconds while a later stage still runs:

1. **Lists**: the PEP lists this instance holds, including the boards of state companies
   (`CZ_ARES_STATE`, `SK_NRSR_VNF`), which come back as findings of kind `BoardSeat`. Implemented.
2. **Boards**: who sits on a board together with a PEP, for close associates. Reported as
   skipped until it exists.
3. **Family**: family members and close associates. Reported as skipped until it exists.

## Sources

| Code | Country | What | Date of birth | Status |
|---|---|---|---|---|
| `CZ_PSP` | CZ | Chamber of Deputies open data (`poslanci.zip`): every deputy since 1993, senators, members of government, presidents, with the dates of each mandate | **yes**, except senators: the Chamber records them without a birthday (the 1900 sentinel), so a senator is a level C finding until senat.cz is read | **implemented** |
| `SK_NRSR` | SK | nrsr.sk: the alphabetical list of deputies per term (3rd term, 2002, onwards) and each deputy's profile, with the date of birth and the town of residence. No mandate dates per person: a membership carries the term's bounds and says so | **yes** | **implemented** |
| `SK_NRSR_VNF` | SK | nrsr.sk register of public officials (ústavný zákon 357/2004): the alphabetical list (one form post) and each official's latest declaration, which names every function with the organisation. Government and state secretaries, constitutional and supreme court judges, the prosecutor general, the central bank's board, mayors and regional chairs, regulators, and the boards of state enterprises and state-owned companies (letter g), ~900 persons). No dates of office: a function carries the declaration year and ends when the person leaves the list | no | **implemented** |
| `CZ_ARES_STATE` | CZ | ARES public register: the statutory bodies and supervisory boards of every state enterprise (legal forms 301, 302, found by the register's search) and of the state-owned companies listed by hand in `StateEntities.json` (ČEZ, ČEPS, ČEPRO, MERO, České dráhy, Letiště Praha, EGAP, ČEB, NRB, PRISKO, Explosia, Thermal-F). Every member with the date of birth and the dates of the seat; seats ended more than ten years ago are left out. Letter g) with level A evidence | **yes** | **implemented** |
| Senators | CZ | senat.cz profiles, for the birthdays the Chamber's data lacks | yes | planned |
| Courts, central bank, ambassadors, parties | CZ | institution web pages | no | planned |

A full list of candidates, what each publishes and why it is or is not usable is in the
analysis in `C:\projects\PepMeld\docs\2026-10-19-analyza.md` (outside the repository).

## Running it

```bash
docker compose up -d db                      # the same server SancMeld uses; PepMeld gets its own database
dotnet ef database update --project src/PepMeld.Core       # creates "pepmeld" and the schema
dotnet run --project src/PepMeld.Server -- import CZ_PSP    # first fill, ~1 650 persons, seconds
dotnet run --project src/PepMeld.Server -- import SK_NRSR   # ~700 profiles; the site answers in 1-4 s, so the first fill takes about 45 min, later runs ~10 min (profiles cached 30 days)
dotnet run --project src/PepMeld.Server -- import SK_NRSR_VNF   # ~1 650 declarations at 1-20 s each, one lane, pausing on 429: the first fill runs overnight, later runs read the cache
dotnet run --project src/PepMeld.Server -- apikey create you@example.com --name dev
dotnet run --project src/PepMeld.Server                     # http://localhost:63463/swagger
```

Every check needs a caller: an API key in `X-Api-Key`, or a Google ID token once
`PepMeld:Auth:GoogleClientId` is set (the same client id SancMeld uses, so one sign-in
will serve both when the front end is connected). A new address gets 50 checks in
total; raise it with `users limit <email> <n>`.

```http
POST /checks
{ "name": "Andrej Babiš", "dateOfBirth": "1954-09-02", "country": "CZ", "reference": "KYC-2026-0412" }
→ 202 { "checkId": "…", "status": "Running", "stages": [ … ] }
→ 409 { "error": "check_in_progress", "checkId": "…" }     one running check per user, enforced in the database
→ 429 { "error": "request_limit_reached" }

GET /checks/{id}        stages, findings, outcome, summary (Slovak, one sentence), coverage
GET /checks/current     the running check or 204
GET /checks?limit=20    history
GET /sources            what is held and when it was last imported (no sign-in needed)
```

Imports run on a timer inside the service (every source older than its `refresh_hours`,
checked hourly); `PepMeld:Jobs:Enabled=false` turns that off. `import <CODE> [--force]`
runs one by hand.

nrsr.sk has no robots.txt and no API. On 2026-10-20 four parallel lanes against the
declaration pages drew a 429 after some 600 pages and then a 403 for the whole site, every
page and every client, from that address. Keep the defaults (one lane, a second between
requests, a month's cache) and run the first fill once, preferably on the server that will
keep importing; a blocked address stays blocked for hours. The person to ask for a
machine-readable export is the Kancelária NR SR, which the User-Agent names.

## Tests

```bash
dotnet test tests/PepMeld.Core.Tests                 # no database
dotnet test tests/PepMeld.Connectors.CzPsp.Tests     # the mapper against fixtures/cz-psp
dotnet test tests/PepMeld.Connectors.SkNrsr.Tests    # the crawl and the parser against fixtures/sk-nrsr
dotnet test tests/PepMeld.Connectors.SkNrsrVnf.Tests # the form post, the declarations and the classification against fixtures/sk-nrsr-vnf
dotnet test tests/PepMeld.Core.IntegrationTests      # needs the compose database; creates pepmeld_tests
```

## Deployment

`Dockerfile.pepmeld` builds the service; it runs migrations on start
(`PepMeld__Database__AutoMigrate=true`) and expects `ConnectionStrings__PepMeld`.
The snapshots of downloaded lists live under `/var/lib/pepmeld/snapshots`; mount a volume.

## Design notes

- **Nothing is deleted.** A function that ends gets `valid_to` and stays: a person remains
  exposed for at least twelve months after it (SK § 12 ods. 3) and "is or was" in Czech law.
  A function that vanishes from a file between imports is closed with that day's date and
  flagged `endedByDisappearance`, because such a date is only as precise as the schedule.
- **A truncated file cannot end everyone's functions.** An import that parses fewer persons
  than `pep_source.min_expected_persons` is refused.
- **Findings are stored as the JSON that was returned**, so a past answer is reproduced,
  not re-rendered from today's data; every check records which lists were searched and
  when they were last imported.
- **The legal basis is text on the finding** (`§ 4 odst. 5 písm. a) zákona č. 253/2008 Sb.`
  and the AMLR 2024/1624 category for 2027), cited at import, not looked up at display time.
