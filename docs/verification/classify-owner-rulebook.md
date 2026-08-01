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

There is **no thirteenth CLASSIFY operation** and **no second evaluator**. The gate
invokes the existing public `classify.rule.validate` operation for:

1. Representative evidence
2. Fresh-key identical replay
3. Separate temporal hold-out evidence

`rule.validate` binds every private row to one complete frozen public Ledger
`classification_v1` evaluation projection (account, description, direction, amount,
lifecycle fingerprint) and returns complete aggregate fingerprints, counters, and
canaries.

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
- All mandatory safety gates pass (or a stable blocked aggregate receipt is emitted)
- Output is aggregate metadata only (no private payloads, paths, or candidate IDs)

Agent policy note: bead implementers must not run unit/filtered/full suites.
Hermes / CI executes the script. Local discovery-only:

```bash
CLASSIFY_OWNER_RULEBOOK_RUN_TESTS=0 bash scripts/verify-classify-owner-rulebook.sh
```

## Owner inputs (untracked)

| Environment variable | Role |
|---|---|
| `CLASSIFY_OWNER_RULEBOOK_CANDIDATE_IDS` | Comma-separated immutable candidate **rule version** IDs |
| `TALLY_DATA_ROOT` | Existing owner runtime root containing the candidate CLASSIFY versions and Ledger projection; retained but never printed |
| `CLASSIFY_OWNER_RULEBOOK_CORPUS` | Owner-only representative JSONL corpus (mode `600`/`400`, not a symlink) |
| `CLASSIFY_OWNER_RULEBOOK_HOLD_OUT` | Owner-only temporal hold-out JSONL (same permission rules) |
| `CLASSIFY_OWNER_DECISIONS_BEFORE` | Aggregate owner decision count before rulebook |
| `CLASSIFY_OWNER_DECISIONS_AFTER` | Aggregate owner decision count after rulebook |
| `CLASSIFY_OWNER_MINUTES_BEFORE` | Optional aggregate owner minutes before |
| `CLASSIFY_OWNER_MINUTES_AFTER` | Optional aggregate owner minutes after |
| `CLASSIFY_OWNER_RULEBOOK_BENEFIT_DECISION` | Explicit product decision when benefit is insufficient (`approve-broad` / `defer-broad`) |

Optional stdin JSON may supply aggregate keys only (for example `benefitDecision`).
**Paths and private payload keys are rejected on stdin.**

Git ignores private CLASSIFY evidence locations (see root `.gitignore`).
**Never** commit personal values, corpora, hold-outs, or receipts containing private rows.

### Missing inputs

When required env vars are unset, the script emits a stable
`VerifiedOwnerRulebookGateReceipt` with:

- `authorityGranted: false`
- `blockCode: CLASSIFY-OWNER-RULEBOOK-INPUT-MISSING`
- zero row totals / canaries
- null fingerprints (derived from absence of evidence)
- no path disclosure

## Aggregate receipt contract (`VerifiedOwnerRulebookGateReceipt`)

Production contract under `Tally.Contracts.Classify.Evidence`.
Every field is **derived** from validation results and owner benefit input.
No hard-coded pass state, zero safety counters, or null fingerprints when evidence exists.

| Field | Meaning |
|---|---|
| `authorityGranted` | `true` only when safety passes on representative + hold-out, replay is deterministic, and benefit is explicitly approved (no invented percentage) |
| `safetyPassed` | Activation-eligible on both evidence sets; zero incorrect applications, unexplained conflicts, drift, unauthorized mutations, description-inferred relationships |
| `benefitSufficient` | Explicit owner product decision only |
| `requiresExplicitOwnerBenefitDecision` | `true` until `approve-broad` is supplied |
| `blockCode` | Stable metadata code when authority is blocked |
| Row totals | `eligible`, `suggested`, `correction`, `noSuggestion`, `conflict`, `excluded`, `stale` |
| Canaries | `incorrectApplication`, `unexplainedConflict`, `drift`, `unauthorizedMutation`, `descriptionInferredRelationship` |
| Fingerprints | `candidate`, `corpus`, `holdOut`, `report`, `outcomesCanonicalHash`, projection `snapshotId` / `storeGenerationFingerprint` |
| `deterministicReplayPassed` | Representative vs fresh-key replay match on deterministic fields |

### Block codes

| Code | Meaning |
|---|---|
| `CLASSIFY-OWNER-RULEBOOK-INPUT-MISSING` | Required owner inputs absent |
| `CLASSIFY-OWNER-RULEBOOK-SAFETY-FAILED` | Incorrect application, conflict, drift, or accounting failure |
| `CLASSIFY-OWNER-RULEBOOK-REPLAY-FAILED` | Deterministic fields differ across replay |
| `CLASSIFY-OWNER-RULEBOOK-HOLD-OUT-FAILED` | Hold-out safety failed |
| `CLASSIFY-OWNER-RULEBOOK-BENEFIT-DECISION-REQUIRED` | Safety passed; broad authority needs explicit owner decision |
| `CLASSIFY-OWNER-RULEBOOK-VALIDATE-UNAVAILABLE` | Public `classify.rule.validate` unavailable or failed closed |

## Public validate result surface

`ClassifyRuleValidateResult` publishes complete aggregate evidence for the gate:

- Fingerprints: candidate, corpus, expected-outcome, projection version, snapshot id/expiry,
  store generation, category lifecycle, normalization, report, outcomes-canonical hash
- Counters: total, accounted, suggestion, no-suggestion, conflict, stale, coverage basis points,
  drift, incorrect-application, unexplained-conflict
- `activationEligible`

Private paths and payloads are never included.

## Named gate families (discovery)

`OwnerRulebookGateTests` exposes at least:

permission, public-contract, projection, 90-day, hold-out, recurrence, timing,
decision-reduction, row-accounting, incorrect-apply, conflict, determinism, drift,
locality, disclosure.

## Live Ledger policy

- Classification projection is **read-only**
- The live operator path retains the caller's owner `TALLY_DATA_ROOT` so candidate
  rule versions and projection rows resolve from one installed composition; it writes
  aggregate CLASSIFY validation evidence but does not mutate Ledger
- No activation, apply, or Ledger category mutation from this gate
- Mutation probes (if any) use disposable `TALLY_DATA_ROOT` only
