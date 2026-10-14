# Name embeddings — does a model reach what the trigram cannot?

An experiment, not a feature. It answers one question with a number: of the confirmed
cross-list pairs the trigram search does not return, how many does a multilingual
embedding model return?

## Why this question

The README records that 56.3 % of genuinely positive pairs are not string-related at
all — `Michail GRIB` ↔ `Міхаіл ГРЫБ`, `JSC FINAM Investment Bank` ↔ `АО Банк ФИНАМ`. A
character-level index cannot reach those however it is tuned, and nothing downstream can
rank a candidate that was never retrieved. An embedding model trained across scripts and
languages might retrieve them. Whether it does on *this* data is what gets measured here,
before anything is built on the assumption.

The ground truth is the same the training corpus is seeded from: the links publishers
declare between their lists (`XrefTrainingSeeder.ResolvedLinksAsync`). France names the EU
designation on 2 051 records and the UN one on 337; the EU names the UN on 83. Two
independent authorities saying "same subject", each having transliterated the name their
own way.

## What it measures

For every resolved link, the citing listing's primary name is searched against the cited
list three ways, and the position of the confirmed listing is recorded:

| Ranking | What it is |
|---|---|
| `pg_trgm` | `DesignationQueryService.SearchAsync`, the matcher as shipped, restricted to the target list |
| embedding | cosine similarity of the query vector against every recorded spelling on the target list, best spelling per listing |
| RRF fusion | reciprocal rank fusion of the two top-k lists — what a combined candidate generator would return |

Reported: recall@1/5/10/k and MRR for each, overall and per list pair, then the three
numbers that decide anything:

- **beyond trigram reach** — links the trigram does not return within k, and how many of
  those the embedding and the fusion do;
- **lost by fusion** — links the trigram found that the fusion pushed past k. A model that
  finds new pairs by losing old ones is the LightGBM result again, and is not an improvement.

Not measured: precision. Both rankings always return k candidates, so the embedding's cost
is k extra rows for the scorer, not extra false positives on screen.

## Running it

Needs the development database with the three lists imported, and Ollama with a model
pulled. Ollama runs natively on Windows, macOS and Linux, or as a container; it needs no
GPU, only patience.

```bash
# once
ollama pull bge-m3

# from the repository root, with the database up and imported
dotnet run --project tools/SancMeld.Experiments.NameEmbeddings -- --model bge-m3
```

The first run embeds every name on the target lists — about 20 000 — and keeps them in
`cache/`, so a second run with a different `k` or target costs seconds. On a CPU expect
20–60 names per second for a 500 M-parameter model.

Models worth comparing, because they were trained for different things:

```bash
# dense multilingual retrieval, 1 024 dims, the strongest open CPU-sized candidate
dotnet run --project tools/SancMeld.Experiments.NameEmbeddings -- --model bge-m3

# multilingual-e5 expects a role prefix on each side and measures worse without it
dotnet run --project tools/SancMeld.Experiments.NameEmbeddings -- \
  --model zylonai/multilingual-e5-large --query-prefix "query: " --name-prefix "passage: "

# a small one, to see what the size buys
dotnet run --project tools/SancMeld.Experiments.NameEmbeddings -- --model paraphrase-multilingual
```

`--target UN_SC` restricts to links into one list, `--limit 200` runs the first 200 links
while something is being wired up, `--help` lists the rest.

Each run writes `results/<model>-<timestamp>.json` with a row per link — both ranks, the
query, the best-scoring spelling and its score — for slicing in ways the printed report
did not anticipate.

## What it measured, 2026-10-14

`bge-m3` through Ollama 0.40.4 on an RTX 3050 (4 GB), k = 25, lists as imported that day
(EU 6 233, UN 1 010, FR 7 079 listings).

### Mode `xref` — 2 411 declared links

| Ranking | recall@1 | recall@10 | recall@25 | MRR |
|---|---:|---:|---:|---:|
| `pg_trgm` | 96.0 % | 98.0 % | 98.0 % | 0.968 |
| embedding | 96.4 % | 98.5 % | 98.6 % | 0.974 |
| RRF fusion | 96.6 % | 98.5 % | 98.6 % | 0.974 |

48 links beyond trigram reach; the embedding reaches 15 of them, the fusion loses none the
trigram had. The 15 are translations of organisation names — `Comité de soutien afghan` ↔
`AFGHAN SUPPORT COMMITTEE`, `ISIL-Asia sudorientale` ↔ `Islamic State East Asia Division`,
`Cyber police` ↔ `Cyber Police (küberpolitsei)` — which is exactly the case a
character-level index cannot serve.

Most of the other 33 are not misses but **wrong links**: France's `REFERENCE_UE` points at
a different person with the same surname often enough to matter. Verified by hand:
France's Ramzan Kadyrov cites `EU.6260.40`, which is the EU's Abuzaid Vismuradov; the EU's
Kadyrov is `EU.226.40`. Nikolay Kozitsyn cites `EU.8491.37`, the EU's is `EU.3539.95`. Some
1.5 % of the French links resolve to the wrong listing, and `seed-xref` currently writes
every one of them into the corpus as a confirmed positive.

This ground truth says little about scripts: France and the EU romanise alike, so nearly
every pair is Latin against Latin. Hence the second mode.

### Mode `cross-script` — 7 981 listings with a hidden non-Latin spelling

| Ranking | recall@1 | recall@10 | recall@25 | MRR |
|---|---:|---:|---:|---:|
| `pg_trgm` | 0.0 % | 0.0 % | 0.0 % | 0.000 |
| embedding | 85.3 % | 94.7 % | **96.3 %** | 0.889 |
| RRF fusion | 85.1 % | 94.7 % | 96.3 % | 0.887 |

Zero for the trigram is by construction — it has no characters in common — and it is the
production behaviour for every listing whose publisher recorded one script only, when the
caller has the other. By the query's script:

| Script | cases | recall@1 | recall@25 |
|---|---:|---:|---:|
| Cyrillic | 6 291 | 92.1 % | 98.9 % |
| Arabic | 1 367 | 61.2 % | 88.6 % |
| Greek | 101 | 92.1 % | 99.0 % |
| Hangul | 115 | 27.8 % | 61.7 % |
| CJK | 72 | 45.8 % | 72.2 % |
| Hebrew | 32 | 59.4 % | 81.2 % |

Cyrillic and Greek are solved; Arabic is usable; Korean and Chinese are not, with this
model. Each query took 8 ms against 41 304 vectors by brute force.

### What it costs without a GPU

The same model in an Ollama container on a Xeon E3-1245 V2 (2012, four cores, AVX only,
no GPU), the kind of host this is likely to run on: a single name embeds in 45–76 ms, a
batch of 32 at 17.4 names/s, so the whole index of 41 304 names takes about 40 minutes
once and a day's import seconds. 2.4 GB of memory. Load on the shared host rose from 0.3
to 1.2 of 8 while it ran.

## Reading the result

The number to look at is **beyond trigram reach → reached by fusion**. If the fusion
reaches a large share of what the trigram misses and loses none of what it finds, a second
candidate generator is worth building, as a union with the trigram and never as a
replacement. The production shape would be ONNX Runtime in the API process and `pgvector`
in the existing database, with the model's name and hash as a row in `match_algorithm`,
because a vector that cannot be recomputed is a decision that cannot be replayed.

If the fusion reaches little, or loses trigram hits, that number goes into the README next
to the LightGBM one and the idea is closed, with the measurement that closed it.

Two caveats on what the ground truth is. The queries are publishers' transliterations, not
what a bank officer types — cleaner, and in a consistent romanisation per publisher. And
the pairs are between *lists*, so a pair the EU and France both spell the same way counts
as a hit for every method; the per-pair tables and the beyond-reach subset are where the
difference lives.
