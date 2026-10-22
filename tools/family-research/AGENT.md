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

The user runs it by hand in a Claude Code window with the personal skill `/pep-family [count]`
(kept in the user's profile, `~/.claude/skills/pep-family`, because it names the server). The
skill does the steps below: queue, one agent per PEP four at a time, upload, import, report.
By hand they are:

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
> Relations to look for, the same for Slovak and Czech PEPs (the wider Czech "osoba blízká"):
> spouse or partner, including a person living with them (current; a former spouse is not a
> family member, record only with status "former"); children and the spouses of children
> ("ChildInLaw"); parents; siblings; grandparents ("Grandparent") and grandchildren
> ("Grandchild"); the spouse's parents ("ParentInLaw"); the spouse's siblings and the
> siblings' spouses ("SiblingInLaw").
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
>    addresses, phone numbers, social media, photos. Minors (under 18) are not recorded at
>    all, not even under "unverified". Record a child or grandchild only when a source shows
>    they are an adult (a date or year of birth 18+ years ago, a stated age of 18 or more, or
>    an adult role such as a job, an office or their own marriage). When the PEP themselves
>    was born before 1990, their children count as adults unless the source suggests
>    otherwise (a later marriage, "little", a school, an age under 18); a grandchild still
>    needs its own evidence.
>    Otherwise leave them out and say in "notes" how many were left out. "isMinor" is
>    always false.
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
>       "relation": "Spouse | Partner | Child | ChildInLaw | Parent | Sibling | Grandparent | Grandchild | ParentInLaw | SiblingInLaw",
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

## Which ties count

One circle for Slovak and Czech PEPs alike, the wider of the two laws (decided 2026-10-20):
the Czech "osoba blízká" of § 4 odst. 5 písm. b) bod 1 zák. 253/2008 Sb. with § 22 of the
Civil Code. The Slovak § 6 ods. 3 zák. 297/2008 Z. z. names only spouse or partner, child,
child's spouse and parent; for siblings, grandparents, grandchildren and in-laws the answer
says that it goes beyond the Slovak law.

## What the import does with it

- A relative without a URL and a quote, or with a relation the law does not name, is refused.
- Every quote is looked for on its page (exact, or a run of at least half its words); the
  result is kept on the evidence (`quoteCheck`). A relative is kept if one quote is found,
  or if none of its pages could be read at all.
- Every tie is level **D** and a candidate until a register or a person confirms it.
- A minor is refused, whether the file says `isMinor: true` or the date or year of birth
  shows it; Wikidata sync skips minors the same way. Decided by the user 2026-10-21: a child
  under 18 cannot be a client or a counterparty on their own, so the tie adds nothing.
- The file itself is kept verbatim on the run (`family_research_run.raw_json`).
- A second run for the same PEP updates the ties it names again and keeps the decisions made on them.

## Monitoring

Every run is kept with its PEP (name and date of birth) and its start and end, so the date
each PEP was last researched is known; `family queue` puts a PEP back in line once that is
older than `--days` (365 by default). Every tie carries `last_found_at`, the end of the last
run that named it, and the answer of a check cites it. A run that no longer names a tie an
earlier run found counts it (`relatives_no_longer_named`) and the import prints it as
"no longer named": a divorce, a death or a search that missed, for a person to look at. Such
a tie is kept, never deleted, with its old date.
