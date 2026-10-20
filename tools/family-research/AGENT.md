# Family research brief

The brief a research agent works from, one PEP at a time, until a model of our own does
this inside PepMeld. Its output is a JSON file that `PepMeld.Server family import` takes
into the database, checking every quote against the page it cites. The files, the checks
and the decisions a person later makes on each tie are also the training data for that
model, so the shape below is a contract: change it only together with `FamilyImportService`.

Measured on the first two runs (2026-10-20, Claude Code agent, web search and fetch):

| PEP | Time | Searches | Pages | Relatives | Quotes found on their page |
|---|---|---|---|---|---|
| Robert Fico | 100 s | 5 | 7 | 7 | 15 / 16 |
| Igor Matovič | 80 s | 5 | 4 | 5 | 8 / 9 |

At about 90 s a PEP, 100 PEPs take two and a half hours one after another, or well under an
hour on four in parallel.

## Daily run

```bash
# 1. who is next: current functions first, most prominent first, not researched in a year
docker exec pepmeld-api dotnet PepMeld.Server.dll family queue 100 > queue.json

# 2. one agent per entry of queue.json, with the brief below; each writes family-<n>.json

# 3. into the database, quotes checked against the web
for f in family-*.json; do
  docker cp "$f" pepmeld-api:/tmp/ && docker exec pepmeld-api dotnet PepMeld.Server.dll family import "/tmp/$(basename "$f")"
done

# 4. what it took
docker exec pepmeld-api dotnet PepMeld.Server.dll family runs
```

## The brief

Replace `{NAME}`, `{DATE_OF_BIRTH}`, `{COUNTRY}`, `{FUNCTIONS}` and `{OUTPUT_PATH}`.

> You are a compliance research agent for an AML PEP-screening tool (EU AML directive
> 2015/849, Slovak Act 297/2008 § 6 ods. 3, Czech Act 253/2008, AMLR 2024/1624). Find the
> FAMILY MEMBERS of one politically exposed person from open, public sources, with
> evidence, and write them to a JSON file. Obliged entities must know who the family
> members of a PEP are.
>
> PEP: {NAME}, born {DATE_OF_BIRTH}, {COUNTRY}. Functions: {FUNCTIONS}.
>
> Relations to look for: spouse or partner (current; a former spouse is not a family member,
> record only with status "former"); children and the spouses of children ("ChildInLaw");
> parents; siblings.
>
> Rules:
> 1. Only claims verified on a page you opened with WebFetch. Every relative needs at least
>    one evidence item: the exact URL and a VERBATIM quote (character for character, original
>    language, at most ~300 characters) that states the relationship. Every quote is checked
>    against its page at import; a relative none of whose quotes is found there is refused.
> 2. Prefer official pages, Wikipedia (sk/cs/en) and major news outlets; say so in "note"
>    when only a tabloid has it.
> 3. A date of birth only if a source states it exactly; otherwise birthYear if stated;
>    never compute one from an age.
> 4. Privacy: name, relation, status, published date or year of birth and evidence only. No
>    addresses, phone numbers, social media, photos. A minor: isMinor true and no birth date
>    or year at all.
> 5. No rumours, alleged partners or speculation; leads you could not confirm go under
>    "unverified".
> 6. At most ~15 searches and ~20 page fetches.
>
> Timing: run `date -u +%Y-%m-%dT%H:%M:%SZ` at the start and at the end; count searches and
> fetches.
>
> Write the result to {OUTPUT_PATH}:
>
> ```json
> {
>   "pep": { "name": "{NAME}", "dateOfBirth": "{DATE_OF_BIRTH}", "country": "{COUNTRY}" },
>   "agent": { "startedAt": "ISO", "finishedAt": "ISO", "searches": 0, "pagesOpened": 0 },
>   "relatives": [
>     {
>       "relation": "Spouse | Partner | Child | ChildInLaw | Parent | Sibling",
>       "name": "Full Name",
>       "dateOfBirth": "YYYY-MM-DD or null",
>       "birthYear": null,
>       "isMinor": false,
>       "status": "current | former | deceased | unknown",
>       "evidence": [ { "url": "https://...", "quote": "verbatim", "publisher": "...", "retrievedAt": "ISO" } ],
>       "confidence": "high | medium | low",
>       "note": "short note in Slovak"
>     }
>   ],
>   "unverified": [ { "relation": "...", "name": "...", "why": "..." } ],
>   "notes": "one or two sentences in Slovak"
> }
> ```

## What the import does with it

- A relative without a URL and a quote, or with a relation the law does not name, is refused.
- Every quote is looked for on its page (exact, or a run of at least half its words); the
  result is kept on the evidence (`quoteCheck`). A relative is kept if one quote is found,
  or if none of its pages could be read at all.
- Every tie is level **D** and a candidate until a register or a person confirms it.
- A minor's date and year of birth are dropped whatever the file says.
- The file itself is kept verbatim on the run (`family_research_run.raw_json`).
- A second run for the same PEP updates the ties it names again and keeps the decisions made on them.
