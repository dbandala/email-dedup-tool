# Senior Engineer Technical Exercise

Thanks for taking the time to do this. It should take **45 to 60 minutes**. Please stop at 60 minutes even if you're not finished; we've deliberately included more than fits in the time, and how you prioritise is part of what we're looking at.

## The situation

You've just joined the ingest platform team. The team owns a small tool that deduplicates a collection batch and writes a load file for the review platform. The functional spec is in `SPEC.md`. The tool is in `src/`.

This ticket landed in your queue this morning:

> **INGEST-4471 — Emails arriving in review without their attachments**
> Reviewers on the Halvorsen matter report emails that show as having attachments in the original mailbox but arrive in review with none. Started after the batch went through dedup. Sample batch that reproduces it is attached (`data/batch-sample.json`). The output the tool currently produces for it is `data/current-tool-output.csv`.

The spec also describes cross-mailbox email deduplication (§3.3), which the tool has never implemented. The team would like both things done.

## What we're asking you to do

1. Diagnose and fix the missing-attachment problem.
2. Implement cross-mailbox email deduplication as described in the spec.
3. Make sure the load file matches the spec.
4. Add tests that cover what you changed.
5. Fill in `DECISIONS.md`. This matters as much as the code. Wherever the spec was unclear, contradictory, or silent, tell us what you decided and why. Tell us what you didn't get to and what you'd do next.
6. Include the transcript(s) of any AI assistant conversations you had while working.

You don't need to finish all of this. You do need to leave the codebase in a state a teammate could pick up.

## Working with AI

Use whatever AI tools you normally use. We assume you will, and we'll read the transcripts. We're interested in how you direct the tool, what you check, and what you push back on, not whether you used it.

## Language

The starter is C# on .NET 8. You're welcome to keep working in C#. If you'd rather work in another language, you may reimplement the tool, provided you preserve the command-line contract (`<tool> <batch.json> <loadfile.csv>`) and the load-file format exactly, and you explain the choice in `DECISIONS.md`. Either way, we'll run your tool on batches you haven't seen.

## Questions

If something in the spec is unclear, you can email your interviewer and we'll reply during business hours. If you'd rather not wait, make a call and write it down in `DECISIONS.md`. Both are fine. Silently guessing is the one thing we'd rather you didn't do.

## Running the starter

```bash
cd src/DedupTool
dotnet run -- ../../data/batch-sample.json out.csv

cd ../..
dotnet test
```

## Disk-backed operation

The CLI stages the input on local disk and processes complete families through a
SQLite-backed index. It keeps processing buffers bounded rather than retaining
the whole batch in memory. It still needs disk proportional to the input,
indexes, temporary files, snapshot, and output; the SQLite cache target is not a
hard limit on process RSS. Output remains sorted by ordinal document ID and the
two positional arguments are unchanged.

The job takes an immutable copy of the input and verifies its SHA-256 digest.
Do not modify the source while a job is running. Input and output must be
different paths. A job interrupted or failed after staging can be resumed by
running the same command with the same input bytes, output path and settings.
Completed stages are retained; an incomplete stage is rebuilt. The CSV is
regenerated after an interrupted export. Changing the input or settings causes
an explicit error rather than reuse of the old job.

Optional environment variables:

| Variable | Default | Description |
|---|---:|---|
| `DEDUP_SCRATCH_ROOT` | Output directory | Existing local directory containing destination-associated job directories. |
| `DEDUP_MAX_DOCUMENT_BYTES` | 67108864 | Maximum buffered JSON token, document, or ignored root-property value, from 1024 through 268435456 bytes. Oversized input fails explicitly. |
| `DEDUP_SQLITE_CACHE_KIB` | 262144 | SQLite page-cache target in KiB, from 1024 through 1048576. This is not a process memory cap. |
| `DEDUP_TRACE` | unset | Set to `1` to print stage timings and checkpoint reuse to stderr. |

Each job directory is named `.deduptool-<destination-hash>`. A persistent
`owner` marker remains after success; a `.deduptool-<destination-hash>.lock`
file in the destination directory coordinates runs even if their scratch roots
differ. Use the same normalized path spelling for a destination: symbolic
links and case-insensitive filesystem aliases are not resolved when choosing
the lock. The snapshot, database, journal and manifest are removed. A failed job
retains these files for resumption. They contain sensitive document metadata and
email text. Restrict access to the scratch root and destination, use local
encrypted storage where appropriate, and account for SQLite temporary files and
the operating system's temporary directory. On Unix the job directory is
restricted to the current user; on Windows configure directory ACLs. Deletion
is not secure erasure. To abandon a failed job, stop all runs for that
destination and manually remove its identified job directory and any associated
`.deduptool-<token>.csv.tmp` file in the destination directory. Never remove
scratch while a job is active.

Output is written to a restricted temporary file in the destination directory,
flushed, and renamed into place only after processing succeeds. Same-directory
rename behavior depends on the filesystem; power-loss durability of directory
metadata is not guaranteed. Input, validation, and processing failures do not
replace a previous output. A cleanup warning means output was published but
sensitive scratch could remain.

The tool still applies the existing heuristic email matching and custodian
policy; see `DECISIONS.md` before using its output for legal production.
The project intentionally remains on .NET 8 for compatibility, but .NET 8
reached end of support on November 10, 2026. Upgrade and validate against a
supported runtime before deployment.

### Opt-in scale benchmark

Use a dedicated machine with sufficient scratch space. The generator streams a
deterministic synthetic workload with duplicated email families and nesting;
other optional profiles are `wide`, `unique`, and `large-body`. The
`large-body` profile writes 128 KiB per email and can consume substantial disk
space. No profile covers every realistic or adversarial distribution.

```bash
dotnet run -c Release --project tools/ScaleGenerator -- 2100000 /path/to/new-batch.json
dotnet run -c Release --project tools/ScaleGenerator -- 100000 /path/to/wide-batch.json wide
dotnet build -c Release src/DedupTool/DedupTool.csproj
/usr/bin/time -lp dotnet src/DedupTool/bin/Release/net8.0/DedupTool.dll \
  /path/to/new-batch.json /path/to/loadfile.csv
```

The `time -lp` options are for macOS; use the platform's equivalent elsewhere.
Record input size, machine, cache configuration, peak RSS, elapsed time, scratch
high-water mark, row counts, and repeated runs. Run representative large-body,
wide/deep-family, mostly-unique and failure workloads as well. A synthetic
result is not a production capacity guarantee.

## What to send back

A zip or repository link containing your code, tests, `DECISIONS.md`, and AI transcripts.

## What happens next

A 25-minute call. You'll walk us through your decisions, we'll run your tool on some additional data together and talk about what happens, and we'll hand you a requirement change and see how you'd approach it.
