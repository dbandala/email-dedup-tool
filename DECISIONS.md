# Decisions

Keep this short and honest. Bullet points are fine. We read this before we read your code.

## Where the spec was unclear, contradictory, or silent

- **§3.2 (attachment hash dedup) vs §3.5 (family integrity).** Read literally, §3.2 suppresses any attachment whose hash was seen before, anywhere in the batch. The ticket shows this breaks §3.5 directly: `DOC-000005` (`vendor-report.pdf` on the "FW" email) was suppressed because `DOC-000003` (the same file on the original email) was seen first, leaving `DOC-000004` without its attachment. I decided family integrity takes precedence: the unit of suppression is the whole rooted family (root plus every descendant), never an individual attachment. A loose root is a family too, including any descendants. Two families are equivalent only if their entire descendant trees match — same type, hash, filename, position, and occurrence count at every node. So two distinct emails that happen to share an attachment, or two matching email roots with different attachment trees, both stay separate and complete. I considered special-casing just the ticket's shape, but that leaves the same bug live for any other batch with a shared attachment across unrelated emails.
- **§3.4 (dedup across the entire batch) vs §4 (custodian self-containment).** Global, cross-custodian suppression satisfies §3.4 but can remove a custodian's only copy of something, which breaks §4's "can be produced on its own if severed" requirement — the fixed CSV schema has no field to record "also collected from," so that loss would be silent and unrecoverable. I split the scope by rule instead of one policy for everything: loose-file suppression (§3.1) is scoped per custodian, since nothing in the spec forces it cross-custodian and doing so only trades disk space for severability risk; email-family suppression (§3.3, cross-mailbox dedup) is scoped globally, because cross-custodian matching is the explicit point of that rule — sender and recipient are almost always different custodians, so a per-custodian scope would make the requested feature a no-op. This still leaves a severed custodian able to lose an email that only survives as another custodian's copy; I flagged that instead of quietly absorbing it.
- **§3.3 cross-mailbox email identity — spec gives no matching algorithm.** The spec says raw bytes differ due to headers/encoding/line endings and that the tool "must recognise" copies as duplicates, but not which fields to match on or how to normalize them. I built the matching key from exactly the variance the spec calls out and nothing more: sent date reduced to a UTC instant, sender lowercased, recipient set lowercased/deduped/sorted, subject trimmed (not stripped of `RE:`/`FW:` — a reply or forward is a genuinely different message and must stay a separate family), and body with CRLF/CR normalized to LF and trimmed. I deliberately avoided fuzzy/similarity matching on subject or body, since it raises false-positive risk with no corroborated benefit here. This is a heuristic, not a guaranteed identity — documented below rather than presented as certain.
- **Malformed/incomplete input — spec is silent on error handling.** The spec describes a well-formed batch but says nothing about missing parents, duplicate IDs, cycles, or incomplete email fields. I chose to fail fast and name the offending `docId` rather than guess a default, since coercing bad input into *some* output risks a load file that looks valid but is wrong in a way nobody catches before it reaches review.

## What I changed and why

- Rewrote `Deduper.cs` from a single global `seen`-hash-per-document check into a graph-based model: build parent/child relationships, compute a bottom-up interned "tree shape" per node (so deep nesting needs no recursion), pick a deterministic winner per equivalence class (lowest ordinal `DocId`), then keep or drop every member of a family together. This is what fixes INGEST-4471 and implements §3.3 in one coherent model rather than bolting cross-mailbox matching onto the old per-document loop.
- Added `EmailIdentity` as the §3.3 matching key (see above), with explicit validation of every field it depends on.
- `Models.cs`: added `[JsonRequired]` to the fields the spec documents as mandatory, so a batch missing them fails at deserialization instead of silently defaulting to `""`/`0`.
- `LoadFileWriter.cs`: added CSV escaping for commas/quotes/line breaks (the spec doesn't ask for it, but `FileName` is operator-supplied free text, and an unescaped comma would silently corrupt column alignment downstream); switched `Write` to stream to a temp file in the destination directory and atomically rename over the target, so a failure partway through never leaves a corrupt or partial load file in place of a good one.
- `Program.cs`: wrapped the run in explicit error handling that reports failure on stderr with a non-zero exit code and never replaces the destination on failure; added a one-line stderr warning when a family mixes custodians, without touching the required one-line stdout success summary from §6.3.
- Replaced/extended `DeduperTests.cs` with `FamilyDeduplicationTests.cs`, `CliTests.cs`, and `LoadFileWriterTests.cs` to cover the ticket scenario, nested/shared attachments, custodian scoping, the email-matching tuple's edge cases, invalid-graph errors, order-independence, CSV escaping, and CLI exit/error behavior.

## What I didn't get to

- The previous in-memory implementation was benchmarked only on a synthetic 200,000-document batch (1.44s, ~220MB peak); that result does not establish capacity at two million.
- A few otherwise-valid ISO 8601 `sentDate` forms are rejected rather than accepted — basic (non-extended) calendar dates and fractional-minute offsets.
- No mechanism to record or review the custodian-severability gap described above — a custodian who loses their only copy during global email-family suppression has no way to recover it, and the fixed schema has no "also collected from" field to even flag it.
- No tooling to review or audit ambiguous/borderline email matches — the heuristic either matches or doesn't, with no human-reviewable "close but not exact" bucket.

## What I'd do next

- Benchmark representative two-million-document and larger batches on a suitable dedicated environment, including unusually large families and message bodies. Establish deployment-specific memory, disk and time budgets before claiming production readiness.
- Raise the custodian-severability gap with the team as a product/schema question — it likely needs a new field (e.g. "collected from") that the current fixed-column load file has no room for, so it isn't something to quietly solve in code alone.
- Extend the ISO 8601 parser to cover the remaining valid formats.
- If the matching heuristic needs to get stricter or looser over time, add a way to surface near-matches for human review rather than only ever emitting a binary dedup decision.

## Disk-backed pipeline and recovery

- The CLI incrementally reads an immutable, digest-verified snapshot and stages
  documents, graph state, tree shapes, and family assignments in SQLite on a
  single machine. The public in-memory `Deduper.Run` remains available for
  compatibility and small-fixture differential tests. SQLite is an embedded,
  local-storage dependency; this is not a distributed or concurrent-writer
  design.
- Bottom-up tree shapes use a SHA-256 candidate index, but exact shape fields
  and child multisets are compared before reusing a shape. Email identities
  are normalized by the same code as the in-memory algorithm. Winner and row
  sorting use big-endian UTF-16 code-unit keys to match .NET ordinal ordering,
  not SQLite's UTF-8 ordering.
- The default maximum buffered JSON token/document is 64 MiB and the default
  SQLite page-cache target is 256 MiB; both are configurable. The token cap
  rejects oversized records and is not an RSS cap. Native allocations, the
  maximum document, SQLite temporary files, runtime overhead, OS cache and
  output buffers also consume resources. Disk usage grows with the input,
  snapshot, database, indexes, journal and output.
- An exclusive destination-associated job lock and durable stage markers
  prevent concurrent use and allow completed stages to be reused. Incomplete
  stages are rebuilt on resume. The manifest binds the job to input digest,
  destination, configuration and pipeline version. Output is regenerated and
  replaced after a failed export; the filesystem and hardware determine
  power-loss guarantees.
- Successful jobs remove document-bearing scratch files, retaining an
  ownership marker and a destination lock file for safe coordination. Failed jobs retain
  potentially sensitive content for resumption. This improves recovery at
  the cost of disk usage, confidentiality exposure, extra I/O and additional
  cleanup responsibilities. Operators must secure scratch and destination
  storage and remove abandoned jobs deliberately.
- The project remains on .NET 8 by request; its support ended November 10,
  2026. Migration to a supported .NET runtime is a separate follow-up.

### Synthetic scale evidence

On a shared Apple M1 machine with 8 GiB RAM, .NET 8.0.11, Release builds,
local storage, and a 64 MiB SQLite cache target:

| Documents | Elapsed | Peak process RSS | Result |
|---:|---:|---:|---|
| 200,000 | 20.02 s | 153,993,216 bytes | 100,001 retained |
| 2,100,000 | 294.59 s | 146,751,488 bytes | 1,050,000 retained |
| 2,100,000 (repeat) | 284.40 s | 142,131,200 bytes | 1,050,000 retained |
| 5,000,000 | 733.11 s | 145,702,912 bytes | 2,500,001 retained |

The two 2.1-million runs produced byte-identical CSV files, each with
1,050,001 lines including the header. These runs used an earlier pipeline
revision and `Microsoft.Data.Sqlite` 10.0.0; the 200,000 and 5,000,000 runs
used 10.0.15. The 5,000,000 run used the same processing algorithm before a
subsequent constructor cleanup change. An independent streaming check of its
2,500,001 output rows verified document IDs, family and parent links,
custodians, dates, filenames and hashes against the generator's expected
result. The 2.1-million scratch directory was sampled at 1.6 GiB and the
5-million directory at 4.0 GiB while running; neither sample is a measured
high-water mark. These are synthetic nested-email workloads on a shared
machine, not a production capacity or service-level guarantee.

## If you reimplemented in another language

N/A — kept C# and the CLI contract, adding the SQLite provider dependency.

## Working with the AI

- Used an AI assistant throughout: diagnosing the ticket against the spec and sample data, designing the family-atomic suppression model, and reasoning through the §3.3 matching key and the §3.4/§4 custodian-scope conflict.
- The AI's first framing of the custodian-scope question presented it as a binary choice (global vs. per-custodian); I pushed for and selected the split-scope resolution (per-custodian for loose files, global for email families) instead of either extreme, since neither one alone satisfies both the explicit §3.3 ask and the §4 severability concern.
- I chose the specific fields and normalization rules in the email-matching tuple myself — notably, not normalizing away `RE:`/`FW:` subject prefixes — after checking against the sample data what actually varies between mailbox copies versus what distinguishes a genuinely different message.
- Would warn a teammate: the AI is good at building out exhaustive test coverage once a design is settled, but the dedup *scope* decisions (per-custodian vs. global, what counts as "the same email") are product/legal judgment calls dressed up as engineering ones. Don't let it quietly pick a default for those — verify against the spec's stated requirements and the actual data before accepting a recommendation.
- Full conversation transcript(s) in `docs/`.
