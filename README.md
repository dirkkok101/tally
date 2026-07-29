# Tally

**A local-first personal finance ledger with a machine-callable command contract.**

Tally tallies financial transactions and provides analytics on budgeting and
performance. It is a single self-contained executable with an embedded local
datastore — no daemons, no network services, no web UI, no cloud account. Every
operation is a discoverable, versioned, JSON-in/JSON-out command, which makes
Tally usable by a person at a terminal *and* directly drivable by an AI coding
agent or automation script.

```console
$ printf '%s' '{"contractVersion":"1.0","actor":{"kind":"automation","label":"agent"},"input":{}}' \
    | tally ledger account list --input -
{"contractVersion":"1.0","operationId":"ledger.account.list","outcome":"success","result":{"items":[...]},"error":null}
```

- **Local-first and private.** Your financial data never leaves the machine. New
  data files and backups default to access by the invoking OS identity only.
- **Agent-native.** 88 public operations, each with a published JSON Schema for
  its request and result, a stable operation id, and a documented exit code.
  Agents discover the contract at runtime instead of guessing flags.
- **Auditable by construction.** Canonical facts, evidence, reconciliation
  decisions, lifecycle history, and attribution changes are retained
  attributably. Identities may be archived, never rewritten.
- **Idempotent mutations.** Every mutation takes a caller-supplied idempotency
  key. Identical reuse returns the original result with zero duplicate effects.

---

## Table of contents

- [What Tally does](#what-tally-does)
- [Status](#status)
- [Requirements](#requirements)
- [Install](#install)
- [Quick start](#quick-start)
- [The command contract](#the-command-contract)
- [Using Tally from an AI agent](#using-tally-from-an-ai-agent)
- [Workflows](#workflows)
- [Data, storage, and recovery](#data-storage-and-recovery)
- [Documentation](#documentation)
- [Development](#development)
- [License](#license)

---

## What Tally does

Tally models personal finances as an append-only ledger of canonical facts, then
layers planning and analysis on top.

| Module | What it does | Operations |
| --- | --- | --- |
| **Ledger** | Accounts, hierarchical categories, transactions, evidence records, payment instruments and cardholders, spend pools, transfers and refunds, statement reconciliation, actuals queries, backup/restore/storage evolution. | 68 |
| **Ingest** | Turns a bank or card statement PDF into a reviewable candidate batch: preview → inspect → approve → commit, with duplicate detection, reconciliation controls, and resumable commits. | 8 |
| **Budget** | Monthly budget plans as immutable draft/active revisions, plus a budget *position* that joins the active plan against ledger actuals (planned / actual / remaining / over, per category). | 6 |
| **System** | Version and compatibility probe, contract discovery (`schema list` / `schema show`), and installable agent guidance bundles. | 6 |

Concepts worth knowing before you start:

- **Evidence** is generic and privacy-conscious: agent capture, statement row,
  receipt, external document, owner assertion. Tally stores stable evidence
  identity, fingerprints, and link history — never mailbox, MIME, messaging,
  credential, or delivery payloads.
- **Reconciliation** compares recorded transactions against statement facts and
  produces explicit states (`recorded_unreconciled`, `statement_reconciled`,
  `statement_only`, `recorded_absent_from_statement`, …) with reviewable
  decisions rather than silent merges.
- **Spend pools**, **payment instruments**, and **cardholders** let you attribute
  a transaction to *who* spent it and *on what instrument*, independently of the
  category it belongs to.
- **Actuals queries** are snapshot-based and cursor-paged, so a long analysis run
  sees one consistent view of the ledger.

### Status

| Module | State |
| --- | --- |
| Ledger | Shipped — v1 contract published and verified |
| Ingest | Shipped — v1 contract published and verified |
| Budget | Shipped — v1 contract published and verified |
| Insights (reports, restatements, retention) | Designed and planned; implementation in progress |
| Classify (automatic transaction classification) | Designed and planned; not yet implemented |

Current version: **0.3.1**, contract version **1.0**. Pre-1.0 the executable
version moves faster than the command contract, and the contract version is the
thing to pin against.

---

## Requirements

- **Linux x64.** Ledger storage enforces Linux host protections; the ledger
  refuses to initialize elsewhere. (`tally version` and `tally schema …` work
  anywhere.)
- **.NET 10 SDK** to build. The published binary is Native AOT and
  self-contained — it has no .NET runtime dependency at run time.

## Install

There are no published release artifacts yet. Build from source:

```bash
git clone https://github.com/dirkkok101/tally.git
cd tally

dotnet publish src/Tally/Tally.csproj \
  -c Release -r linux-x64 \
  --self-contained true -p:PublishAot=true \
  -o ./dist
```

That produces a single executable at `./dist/tally`. Put it on your `PATH`:

```bash
install -m 0755 ./dist/tally ~/.local/bin/tally
tally version
```

```json
{"contractVersion":"1.0","operationId":"system.version","outcome":"success","result":{"product":"tally","version":"0.3.1","contractVersion":"1.0","compatibility":"1.0"},"error":null}
```

### Point Tally at a data directory

All ledger, ingest, and budget operations require `TALLY_DATA_ROOT`. Tally
creates and initializes the store on first use.

```bash
export TALLY_DATA_ROOT="$HOME/.local/share/tally"
mkdir -p "$TALLY_DATA_ROOT"
```

Without `TALLY_DATA_ROOT`, only the `system.*` operations are available — which
is exactly what an agent needs for contract discovery.

---

## Quick start

Every example below is a real, working invocation.

**1. Discover the contract.**

```bash
tally help                              # same as: tally schema list
tally schema show ledger.account.create
```

`schema show` returns the CLI path, whether the operation is a query or a
mutation, whether it needs an idempotency key, the request and result JSON
Schemas, and the full list of error codes with their exit codes.

**2. Create an account.**

```bash
printf '%s' '{
  "contractVersion": "1.0",
  "actor": {"kind": "human", "label": "dirk"},
  "idempotencyKey": "account-daily-cheque",
  "input": {
    "institutionName": "Example Bank",
    "displayName": "Daily",
    "accountType": "cheque",
    "maskedIdentifier": "****1234",
    "currencyCode": "ZAR"
  }
}' | tally ledger account create --input -
```

```json
{"contractVersion":"1.0","operationId":"ledger.account.create","outcome":"success",
 "result":{"accountId":"01KYPTAMVYF08E3YJ81EZYH46D","displayName":"Daily","accountClass":"asset",
           "status":"active","createdActor":"human:dirk","lifecycleHistory":[…]},"error":null}
```

**3. Create a category.**

```bash
printf '%s' '{
  "contractVersion": "1.0",
  "actor": {"kind": "human", "label": "dirk"},
  "idempotencyKey": "category-groceries",
  "input": {"name": "Groceries"}
}' | tally ledger category create --input -
```

**4. Record a transaction** (substituting the ids you got back):

```bash
printf '%s' '{
  "contractVersion": "1.0",
  "actor": {"kind": "human", "label": "dirk"},
  "idempotencyKey": "txn-checkers-2026-07-15",
  "input": {
    "accountId": "01KYPTAMVYF08E3YJ81EZYH46D",
    "signedAmount": "-450.20",
    "currencyCode": "ZAR",
    "transactionDate": "2026-07-15",
    "postingDate": null,
    "originalDescription": "CHECKERS HYPER",
    "instrumentId": null,
    "cardholderId": null,
    "initialEvidence": {
      "kind": "agent_capture",
      "logicalIdentityDigest": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "opaqueExternalReference": null,
      "contentFingerprint": null,
      "observation": null
    }
  }
}' | tally ledger transaction record --input -
```

**5. Categorize it, then query actuals.**

```bash
printf '%s' '{"contractVersion":"1.0","actor":{"kind":"human","label":"dirk"},
  "idempotencyKey":"assign-txn-1",
  "input":{"transactionId":"<TXN>","categoryId":"<CAT>","reason":"Groceries purchase"}}' \
  | tally ledger transaction category assign --input -

printf '%s' '{"contractVersion":"1.0","actor":{"kind":"human","label":"dirk"},"input":{}}' \
  | tally ledger actuals query --input -
```

```json
{"result":{"snapshotId":"01KYPTANCR773GNWQBGGCRX8W4","totalCount":1,
  "items":[{"transactionId":"…","effectiveDate":"2026-07-15","categoryState":"categorized",
            "reconciliationState":"recorded_unreconciled",
            "contribution":{"netAccountMovement":"-450.20",…}}],
  "totals":{"netAccountMovement":"-450.20","externalSpend":"-450.20","budgetActual":"-450.20"}}}
```

---

## The command contract

Tally has no conventional flags. There are exactly three surface elements: the
**command path**, the **`--input` selector**, and the **JSON envelope**.

### Invocation

```
tally <command path…> --input -            # request JSON on stdin
tally <command path…> --input @/path.json  # request JSON from a file
tally version | tally help | tally schema show <operation-id>
```

`--input` must be the last argument, and its value must be `-` or `@<path>`.
When reading from stdin, close it (pipe or redirect) — Tally reads to end of
input.

### Request envelope

```json
{
  "contractVersion": "1.0",
  "actor": { "kind": "human | automation | system", "label": "…", "runId": "…" },
  "idempotencyKey": "…",
  "input": { }
}
```

- `contractVersion` must be `"1.0"`.
- `actor.label` and `actor.runId` are restricted to letters, digits, `.`, `-`,
  `_`, max 128 characters. The actor is recorded on every mutation as
  `kind:label[:runId]` — e.g. `human:dirk` or `automation:agent:run-7`.
- `idempotencyKey` is **required for mutations and forbidden for queries** —
  `schema show` tells you which via `requiresIdempotencyKey`. Validation
  failures never consume an idempotency key, so an agent can correct the input
  and retry with the same key.
- `input` must match the operation's published request schema exactly. Unknown
  properties are rejected.

### Result envelope

Success and failure use the same shape on stdout, always a single line:

```json
{"contractVersion":"1.0","operationId":"ledger.account.create","outcome":"success","result":{…},"error":null}
{"contractVersion":"1.0","operationId":"system.process","outcome":"error","result":null,
 "error":{"code":"LEDGER-ACCOUNT-DUPLICATE","category":"conflict","message":"…","fields":null}}
```

On error, stderr carries one line — `tally: <code>` — and nothing else. Results
never leak credentials, keys, unmasked bank identifiers, or raw statement rows.

### Exit codes

| Code | Category | Meaning |
| ---: | --- | --- |
| 0 | — | Success |
| 2 | `usage` | Unknown operation or malformed `--input` |
| 3 | `validation` | Input does not satisfy the published schema or domain rules |
| 4 | `not_found` | Target does not exist |
| 5 | `conflict` | Conflicts with current state (duplicates, stale digests, idempotency conflict) |
| 6 | `lifecycle` | Current lifecycle state forbids the operation |
| 7 | `compatibility` | Request, cursor, or artifact is incompatible with this contract |
| 8 | `integrity` | Cannot preserve an integrity contract; explicit review required |
| 9 | `host` | Host could not safely complete the operation |
| 10 | `host` | Unexpected failure |

Ingest reuses codes 5 and 6 with extra, more specific categories —
`unsupported`, `unsafe_source`, `overlap`, and `reconciliation` on 5;
`resource`, `ledger`, and `interrupted` on 6. Always read `error.category`
alongside the exit code, and take the per-operation list from `schema show`.

---

## Using Tally from an AI agent

Tally is designed to be driven by an agent that has never seen it before. The
loop is: **probe the version → list operations → read the schema → invoke → read
the envelope.**

### Install the guidance bundle

Tally ships embedded skill bundles for Claude Code and Codex and can install them
into a project:

```bash
printf '%s' '{"contractVersion":"1.0","actor":{"kind":"human","label":"dirk"},
  "input":{"scopePath":"/path/to/project"}}' \
  | tally system guidance list --input -

printf '%s' '{"contractVersion":"1.0","actor":{"kind":"human","label":"dirk"},
  "idempotencyKey":"guidance-claude-1",
  "input":{"host":"claude-code","scopePath":"/path/to/project"}}' \
  | tally system guidance install --input -
```

| Host | Installed to |
| --- | --- |
| `claude-code` | `<scopePath>/.claude/skills/tally-ledger/SKILL.md` |
| `codex` | `<scopePath>/.agents/skills/tally-ledger/SKILL.md` |

`guidance list` reports each bundle's `status` (e.g. `missing`) plus the
executable and contract version range it is valid for. `guidance check`
re-verifies an installed bundle against the embedded checksum.

The bundle is deliberately thin. It grants no authority and adds no operations —
it only tells the agent to treat the executable contract as authoritative:

> 1. Run `tally version` and require executable and contract version `1.0`.
> 2. Run `tally schema list` to discover public operation identifiers.
> 3. Run `tally schema show <operation-id>` before constructing a request.
> 4. Invoke only the published command path using a closed JSON request envelope on standard input.
> 5. Read the structured result envelope and handle the published stable error codes.
>
> Do not infer operations, input fields, defaults, or validation rules beyond the discovered schema.

### Rules for agent authors

- **Never hand-write a request from memory.** Call `schema show` and build the
  input from `requestSchema`. Unknown properties are a hard validation failure.
- **Set `actor.kind` to `"automation"`** and supply a stable `runId`, so the audit
  trail distinguishes agent work from human work.
- **Derive idempotency keys from the work item**, not from a clock or a random
  value, so a retried step is a genuine replay.
- **Branch on the exit code, not on message text.** Messages are intentionally
  generic; `error.code` and `error.category` are the stable contract.
- **Treat exit code 8 (`integrity`) as "stop and ask".** It means the operation
  needs explicit human review before any financial effect changes.
- **Respect snapshot expiry.** `ledger actuals query` and `ingest status` return a
  `snapshotId` with an `expiresAt`; paging past expiry fails rather than silently
  reading a different ledger state.

---

## Workflows

### Statement ingestion

Ingest never writes to the ledger without an explicit, digest-checked approval.

```
ingest preview  →  ingest inspect  →  ingest approve  →  ingest commit
   (parse)          (review rows)      (lock digest)      (write ledger)
```

```bash
printf '%s' '{
  "contractVersion": "1.0",
  "actor": {"kind": "automation", "label": "agent", "runId": "july"},
  "input": {
    "contractVersion": "1.0",
    "sourcePath": "/abs/path/statement.pdf",
    "accountId": "01KYPTAMVYF08E3YJ81EZYH46D",
    "actor": {"kind": "automation", "label": "agent", "runId": "july"}
  }
}' | tally ingest preview --input -
```

```json
{"result":{"batchId":"60941ad6…","manifestRevisionId":"593931d0…","status":"previewed",
  "adapter":"pdf-text-layout-a-v1",
  "counts":{"acceptedCandidates":14,"exactDuplicates":0,"excludedNonTransactions":2,"blocked":0},
  "reconciliationSummary":{"fullyReconciled":true,"controls":[
     {"name":"record_accounting","satisfied":true},{"name":"opening_to_closing","satisfied":true},…]}}}
```

- Statement adapters are **provider-neutral PDF text layouts**
  (`pdf-text-layout-a-v1`, `pdf-text-layout-b-v1`) selected by document shape,
  not by bank name. An unrecognized or ambiguous layout fails with a distinct
  code rather than guessing.
- `approve` and `commit` require the `manifestDigest` from `inspect`. If the
  preview or the underlying ledger changed, they fail with a conflict instead of
  committing stale candidates.
- Commits are resumable: an interrupted commit reports
  `INGEST-COMMIT-INTERRUPTED`, and `ingest resume` finishes it. `ingest status`,
  `ingest abandon`, and `ingest cleanup` cover the rest of the batch lifecycle.
- Preview refuses sources that are unreadable, oversized, changed mid-read, or
  blocked by overlap/reconciliation policy — each with its own error code.

### Budget planning and position

```bash
# 1. Draft a plan revision for a period
printf '%s' '{"contractVersion":"1.0","actor":{"kind":"human","label":"dirk"},
  "idempotencyKey":"budget-2026-07-draft",
  "input":{"contractVersion":"1.0",
           "period":{"year":2026,"month":7,"currencyCode":"ZAR"},
           "entries":[{"categoryId":"<CAT>","plannedMinorUnits":600000}],
           "reason":"July grocery plan"}}' \
  | tally budget plan draft create --input -

# 2. Activate it
printf '%s' '{"contractVersion":"1.0","actor":{"kind":"human","label":"dirk"},
  "idempotencyKey":"budget-2026-07-activate",
  "input":{"contractVersion":"1.0","revisionId":"<REV>","reason":"Approved for July"}}' \
  | tally budget plan revision activate --input -

# 3. Read planned vs actual
printf '%s' '{"contractVersion":"1.0","actor":{"kind":"human","label":"dirk"},
  "input":{"contractVersion":"1.0",
           "period":{"year":2026,"month":7,"currencyCode":"ZAR"},"revisionId":null}}' \
  | tally budget position get --input -
```

```json
{"result":{"position":{"calculationSchemaVersion":"budget-position-v1","revisionStatus":"active",
  "period":{"year":2026,"month":7,"currencyCode":"ZAR",
            "startInclusive":"2026-07-01","endExclusive":"2026-08-01","state":"current"},
  "categoryPositions":[{"currentDisplayName":"Groceries","kind":"budgeted",
    "plannedMinorUnits":600000,"actualMinorUnits":45020,
    "remainingMinorUnits":554980,"overMinorUnits":0}]}}}
```

Amounts in budget requests and results are **integer minor units**; ledger
transaction amounts are decimal strings (`"-450.20"`). Revisions are immutable —
correcting a plan means drafting and activating a new revision, and the position
records which revision it was computed against.

---

## Data, storage, and recovery

`TALLY_DATA_ROOT` looks like this:

```
$TALLY_DATA_ROOT/
  CURRENT                    # pointer to the active ledger generation
  generations/<id>/          # ledger SQLite generation
  ingest/ingest.db           # ingest batches and manifests
  budget/budget.db           # budget plans and revisions
```

Files are created owner-only. `ledger storage status` reports the schema version,
generation id, fingerprints, and whether owner-only permissions, integrity, and
host protections verified:

```bash
printf '%s' '{"contractVersion":"1.0","actor":{"kind":"human","label":"dirk"},"input":{}}' \
  | tally ledger storage status --input -
```

```json
{"result":{"schemaVersion":3,"storageContractVersion":"2","currentGenerationId":"67c076fe…",
  "ownerOnlyPermissions":true,"integrityVerified":true,"hostProtectionVerified":true}}
```

Recovery operations follow a **prepare-then-activate** pattern so a bad artifact
can never become live implicitly:

| Operation | Purpose |
| --- | --- |
| `ledger backup create` / `ledger backup verify` | Create a checksummed backup; verify it independently |
| `ledger restore prepare` / `ledger restore activate` | Stage a restore candidate, then atomically activate it |
| `ledger storage evolution prepare` / `… activate` | Stage a schema upgrade as a new generation, then activate it |

Nothing here talks to the network. There is no telemetry, no sync, and no
account.

---

## Documentation

Honest summary: the *engineering* documentation is unusually deep, and the
*end-user* documentation is this README.

**What exists today**

- **This README** — installation, contract, and worked examples for every
  shipped module.
- **The executable contract itself** — `tally help` and `tally schema show
  <operation-id>` are the authoritative, always-current reference for all 88
  operations. Prefer them over any prose.
- **`docs/plans/`** — implementation plans for all five modules (Ledger, Ingest,
  Budget, Insights, Classify), exported from the design graph: 27 sub-plans and
  189 tasks with constraints and acceptance criteria. This is the bulk of the
  written documentation.
- **`docs/verification/`** — what each module verification gate proves, and how.
- **`docs/reviews/`, `docs/diagnosis/`, `docs/validation/`** — adversarial review
  records, bug diagnoses, and resolved open questions.
- **`AGENTS.md`** — how the SDLC pipeline for this repo works.

Requirements, designs, decisions, and plans are authored in a
[lex](#development) graph under `.lexicon/`: 79 functional requirements, 36
non-functional requirements, 51 design decisions, 55 data models, 120 test cases,
30 accepted ADRs, 33 patterns, and 2,303 cross-entity links across seven modules.
Markdown under `docs/` is **generated output** — edit the graph entities and
re-export, never the Markdown.

**Known gaps**

- **PRDs, technical designs, ADRs, patterns, and architecture narratives are not
  exported.** `docs/prd/`, `docs/designs/`, `docs/adr/`, `docs/patterns/`, and
  `docs/architecture/` are empty scaffolding — the content lives in the lex graph
  and is only readable via `lex` (`lex fr list --module LEDGER --json`,
  `lex adr list --module CORE --json`, …). Reading the design rationale currently
  requires the `lex` toolchain.
- No task-oriented user guides beyond this README — no "import your first
  statement end to end" tutorial, no reconciliation walkthrough.
- No published release binaries, no CI workflow in the repo, and no changelog.

---

## Development

```bash
dotnet build                    # build solution
dotnet test                     # run the full suite (4,182 tests)
```

Module verification gates live in `scripts/`. Each one publishes a Native AOT
binary and runs the relevant black-box suite against the real executable:

```bash
scripts/verify-ledger-core.sh
scripts/verify-ledger-module.sh
scripts/verify-ledger-security.sh
scripts/verify-budget-module.sh
scripts/verify-budget-contract.sh
scripts/verify-budget-graph.sh
scripts/verify-budget-performance.sh
scripts/verify-budget-recovery.sh
scripts/verify-budget-security.sh
```

Tests can be pointed at an already-published binary:

```bash
TALLY_PUBLISHED_BINARY=./dist/tally dotnet test tests/Tally.Tests/Tally.Tests.csproj
```

**Stack and deliberate constraints.** .NET 10, Native AOT, linux-x64, raw
`Microsoft.Data.Sqlite`, source-generated JSON, PdfPig for statement text, xUnit
against real temporary stores. No ASP.NET Core, no EF Core, no HTTP endpoints, no
hosted services, no reflection scanning, no mediator or event bus, no custom
cryptography, no plugin runtime. Adding an operation means extending the
published contract, its schema, its error codes, and its tests together.

Requirements, designs, plans, and ADRs are authored with **lex**, and work items
are tracked with **br** (`beads_rust`); see `AGENTS.md` for the pipeline.

## License

[MIT](LICENSE)
