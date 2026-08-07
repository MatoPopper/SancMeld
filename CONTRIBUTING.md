# Contributing to SancMeld

Thanks for considering it. The most valuable contribution is a **new source connector** —
especially a national list we have not covered yet.

## Ground rules

Contributions are accepted under the [Developer Certificate of Origin](https://developercertificate.org/).
Sign your commits:

```bash
git commit -s -m "Add Estonian sanctions connector"
```

No CLA is required: the project is Apache-2.0, which already permits the uses we need.

## Before you write a connector

Sanctions data is compliance data. A connector that silently returns partial results is
worse than no connector at all. Before opening a PR, please confirm:

1. **The domain is official.** A ministry or supervisory authority, not a news aggregator.
2. **You know what the list legally is.** An asset-freeze list and an entry-ban list are
   different things and must not be conflated — see `MeasureType` in the domain model.
   A person banned from entering a country is not automatically someone whose assets
   must be frozen.
3. **You have checked whether it is really a designation list.** Latvia, for example,
   publishes a register of assets frozen *in Latvia*, which is a different thing from
   a Latvian designation list.
4. **You have a stable record key.** If the publisher provides no id, document the
   deterministic fingerprint you derive and bump `FingerprintVersion` when the recipe changes.

## Writing the connector

Implement `ISanctionsConnector`. Four methods, deliberately separated:

- `DiscoverAsync` — resolve the current download URL. Keep this apart from downloading:
  attachment URLs change (Poland uses a GUID, Czechia a dated filename).
- `DownloadAsync` — always send a `User-Agent`; some publishers return 403 without one.
  Support conditional requests so an unchanged file is not reparsed.
- `ParseAsync` — stream. Do not load the document into memory; some are over 100 MB.
- `ValidateAsync` — reject implausible datasets. This is what stops a truncated download
  from wiping the live list.

### Checklist

- [ ] A real downloaded sample committed to `fixtures/<SOURCE_CODE>/`
- [ ] A regression test that parses that fixture and asserts the record count and a few fields
- [ ] `min_expected_records` set for the source
- [ ] Original text preserved alongside normalised values (`country_original`, `value_original`)
- [ ] Dates mapped to `DatePrecision`, never coerced into a bare date
- [ ] Measures typed correctly (`AssetFreeze` vs `EntryBan`)
- [ ] Legal basis and source URL captured
- [ ] `raw_record` populated with the verbatim source fragment

### Things that will get a PR rejected

- Regex parsing of HTML — use AngleSharp or HtmlAgilityPack
- Loading a whole document into a `string`
- Dropping records that fail to parse without counting them
- Deleting and reinserting instead of versioning
- Any credential, token or connection string in the repository

## Running the tests

Unit tests need nothing. The ingest tests need a real PostgreSQL, because the behaviour
they check — one open revision per record, jsonb staging, `ExecuteUpdate` — is enforced by
the database rather than by C#.

```bash
docker compose up -d db     # they create and migrate their own sancmeld_tests database
dotnet test
```

Point them elsewhere with `SANCMELD_TEST_CONNECTION`. They never touch the development
database, only one of their own.

## Code style

- `dotnet format` before pushing; warnings are errors
- Comments explain **why**, not what. If a line encodes a legal or data-quality subtlety,
  say so — the next person will not know that OFAC marks empty fields as `-0-`.
- Match the surrounding code's density and idiom

## Documentation

Document a decision where it takes effect: an XML doc comment on the option, a comment at
the code that encodes the subtlety, or the README when it changes how the project is used.
Claims should be backed by a measurement — the defaults in this project cite real numbers,
and we would like to keep it that way.

## Reporting a source that broke

Open an issue with the source code (`EU_FSF`, `PL_NATIONAL`, …), the HTTP status or parse
error, and the date you observed it. Government endpoints change without notice; these
reports are genuinely useful.

## Security

Do not open a public issue for a security problem. Email the maintainers instead.
