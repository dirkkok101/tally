# CLASSIFY owner-rulebook pre-authority gate

Status: verification gate for `TASK-CLASSIFY-RULEBOOK-GATE-OWNER-RULEBOOK` /
`TC-CLASSIFY-OWNER-RULEBOOK-PRE-AUTHORITY-GATE` / `FR-CLASSIFY-RULE-VALIDATION` /
`NFR-CLASSIFY-LOCAL-DATA-PROTECTION` / `bd-56yx`.

This document is **metadata-only**. It describes the operator procedure, aggregate
receipt contract, and mandatory safety families. It must **never** contain private
corpus paths, transaction descriptions, normalized tokens, amounts, expected-outcome
rows, personal rule values, or raw evaluation payloads.

## Purpose

Prove that an **owner-authored** deterministic rulebook is safe, deterministic,
privacy-preserving, and useful **before** any rule can become active and before any
CLASSIFY apply workflow gains authority.

The failed automatic-discovery experiment remains **historical evidence only**.
This gate does not rediscover rules, seed values, broaden grammar, or invent a
50% benefit threshold.

## Gate command

```bash
bash scripts/verify-classify-owner-rulebook.sh
```

Expected:

- Exit 0
- Linux host
- Release build succeeds
- ≥12 named gate families discovered under `OwnerRulebookGateTests`
- All mandatory safety gates pass
- Output is aggregate metadata only (no private payloads or paths)

Agent policy note: bead implementers must not run the full unit matrix; Hermes /
CI executes the script. Local discovery-only:

```bash
CLASSIFY_OWNER_RULEBOOK_RUN_TESTS=0 bash scripts/verify-classify-owner-rulebook.sh
```

## Owner inputs (untracked)

| Environment variable | Role |
|---|---|
| `CLASSIFY_OWNER_RULEBOOK_CORPUS` | Owner-only 90-day representative JSONL corpus (mode `600`/`400`, not a symlink) |
| `CLASSIFY_OWNER_RULEBOOK_HOLD_OUT` | Owner-only temporal hold-out JSONL (same permission rules) |
| `CLASSIFY_OWNER_RULEBOOK_BENEFIT_DECISION` | Optional explicit product decision when benefit is insufficient (`approve-broad` / `defer-broad`) |

Git ignores private CLASSIFY evidence locations (see root `.gitignore`).
**Never** commit personal values, corpora, hold-outs, or gate receipts containing
private rows.

### Missing inputs

When corpus or hold-out env vars are unset, the script emits a stable
`VerifiedOwnerRulebookGateReceipt` with:

- `authorityGranted: false`
- `blockCode: CLASSIFY-OWNER-RULEBOOK-INPUT-MISSING`
- zero row totals / canaries
- no synthesized fingerprints
- no path disclosure

Synthetic unit tests still prove accounting, canaries, determinism, privacy, and
blocked-input behavior without owner private data.

## Aggregate receipt contract (`VerifiedOwnerRulebookGateReceipt`)

| Field | Meaning |
|---|---|
| `authorityGranted` | Must be `false` unless every safety gate passes **and** owner benefit is sufficient or explicitly approved |
| `safetyPassed` | Zero incorrect applications, unexplained conflicts, unauthorized mutations, description-inferred relationships; accounting complete; determinism + drift pass |
| `benefitSufficient` | Owner-decision / elapsed-time evidence judged sufficient for broad authority |
| `requiresExplicitOwnerBenefitDecision` | `true` when benefit is insufficient — **no invented percentage threshold** |
| `blockCode` | Stable metadata code when authority is blocked |
| Row totals | `eligible`, `suggested`, `correction`, `noSuggestion`, `conflict`, `excluded`, `stale` |
| Canaries | `incorrectApplication`, `unexplainedConflict`, `drift`, `unauthorizedMutation`, `descriptionInferredRelationship` |
| Fingerprints | Candidate / corpus / hold-out SHA-256 hex only (never raw rows) |
| Benefit | Owner decisions before/after; optional minutes before/after |

## Named mandatory gate families

| Gate needle | Proves |
|---|---|
| `Gate_permission` | Owner-only corpus modes; symlink rejection |
| `Gate_public_contract` | Evidence uses public Ledger classification projection surface (no private Ledger SQL) |
| `Gate_90_day` | Representative corpus window metadata (aggregate) without private rows |
| `Gate_hold_out` | Hold-out partition is separate and accounted |
| `Gate_recurrence` | Recurring description canaries stay deterministic under owner-authored equals rules |
| `Gate_timing` | Elapsed-time benefit fields are aggregate-only |
| `Gate_decision_reduction` | Owner decision before/after counts without inventing a 50% bar |
| `Gate_row_accounting` | Eligible/suggested/correction/no-suggestion/conflict/excluded/stale partition |
| `Gate_incorrect_apply` | Incorrect-application canaries block authority |
| `Gate_conflict` | Unexplained incompatible conflicts block; expected conflicts are explained |
| `Gate_determinism` | Identical fingerprints → identical ordered outcomes |
| `Gate_drift` | Drift canaries (stale membership) fail safety |
| `Gate_locality` | Mutation probes use disposable `TALLY_DATA_ROOT` only |
| `Gate_disclosure` | Paths, descriptions, tokens, amounts, expected outcomes absent from receipts/diagnostics |

Additional canary shapes exercised in tests (aggregate labels only): mixed, sign,
account, fee, transfer, refund, shared-medical — without storing private text in
tracked artifacts.

## Production seams (consume only)

- `ValidateClassificationRuleCommand.HandleAsync` — real rule validation lifecycle
- `PrivateCorpusReader.ReadAsync` — owner-only JSONL boundary
- `ClassificationEngine.Evaluate` — production deterministic engine
- `LedgerContractClient.QueryClassificationProjectionAsync` / category list — public Ledger
- `OwnerRulebookGateInputManifest` / `OwnerBenefitEvidenceReceipt` — aggregate EXT corpus types

Do **not** add a second evaluator, broaden grammar, discover rules, or treat
coverage alone as authority.

## Live Ledger policy

- Live Ledger is **read-only** for this gate.
- Any mutation probe uses a **disposable** `TALLY_DATA_ROOT`.
- Unauthorized mutations must remain zero.

## Human review (benefit only)

When safety passes but measured owner-decision or time benefit is insufficient:

1. Do **not** invent a 50% threshold.
2. Set `requiresExplicitOwnerBenefitDecision = true`.
3. Record the owner product decision via `CLASSIFY_OWNER_RULEBOOK_BENEFIT_DECISION`
   before broad authority or module completion continues.

## How to re-run

```bash
# Discovery-only (no suite execution)
CLASSIFY_OWNER_RULEBOOK_RUN_TESTS=0 bash scripts/verify-classify-owner-rulebook.sh

# Full gate (Hermes / CI / owner machine)
bash scripts/verify-classify-owner-rulebook.sh

# With owner private inputs (paths never printed)
export CLASSIFY_OWNER_RULEBOOK_CORPUS="$HOME/.classify-private/corpus-90d.jsonl"
export CLASSIFY_OWNER_RULEBOOK_HOLD_OUT="$HOME/.classify-private/holdout.jsonl"
export CLASSIFY_OWNER_RULEBOOK_BENEFIT_DECISION=defer-broad   # or approve-broad
bash scripts/verify-classify-owner-rulebook.sh
```

## Result log (metadata only)

Record exit code, discovered case count, and whether live owner inputs were
`present` or `blocked`. Do not paste private payloads or absolute secret paths.

| Field | Value |
|---|---|
| Latest bead | `bd-56yx` |
| Commit subject | `test(classify): rulebook gate owner rulebook` |
| Live owner path | blocked unless env provided |
| Authority without owner inputs | denied |
