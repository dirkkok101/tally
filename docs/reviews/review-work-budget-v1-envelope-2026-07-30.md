# BUDGET v1 work review (envelope extension)

Date: 2026-07-30

Plan: `PLAN-BUDGET-V1` (29 tasks / 29 beads closed)

Reviewed commit (post-fix): `aec2e6cee9bb5745e34cd19667b8060a52609ff5`

Prior full review: `docs/reviews/review-work-budget-v1-2026-07-28.md` (spec + quality PASS; proof harvest at `aba0d54`)

This review re-enters after the **category envelope resolution** wave (`bd-1xlf` … `bd-zsby` / epic `bd-3lrk`).

## Verdict

| Axis | Verdict | Notes |
|---|---|---|
| **Spec conformance** | **PASS** (converged) | All 29 plan beads closed; envelope SCs mapped to code + TC-linked tests; one mechanical AC gap fixed |
| **Code quality** | **PASS** (converged) | Ordinal absorption order fixed; remaining residuals are WARN / filed ambition |

Open must-fix after converge: **0**.

## Scope

| Item | Value |
|---|---|
| Plan beads | 29 (all `br` closed) |
| Envelope feature commits | `43bab99` … `f7203c3` + close chores |
| Fix commit this review | `fix(budget): order envelope absorption by member ordinal` (`Refs: bd-3uzt`) |
| Untraced envelope commits | None for feature footers (chore close commits are ledger only) |

## Phase 1 — Build & test (fresh)

| Gate | Result |
|---|---|
| `dotnet build Tally.slnx` | **0 errors, 0 warnings** |
| Budget suite excl. personal-scale perf | **684 passed, 0 failed** (~38s) |
| Envelope unit suites after fix | **65 passed** (resolution + integrity + calculator) |
| `bash scripts/verify-budget-fast.sh` | **exit 0** (~14–27s; 69 core tests) |
| Personal-scale perf | Not re-run this review (prior review + module run 2026-07-29 green at renegotiated NFR; tracked ambition `bd-12td`) |

## Phase 2 — Bead satisfaction

### Envelope wave (deep)

| bead | overall | evidence |
|---|---|---|
| bd-1xlf ENVELOPE-CONTRACTS | **PASS** | `BudgetPositionContracts` partition fields; `BudgetActualMember` ancestry/effective; `CalculationSchemaVersion=budget-position-v2`; `BudgetContractShapeTests` + TC shape |
| bd-tuey ANCESTRY-COMPOSITION | **PASS** (WARN) | Mapper carries `FrozenAncestryIds`; query/insights add ancestry to `requiredIds`. WARN: no dedicated test asserting required-id set assembly / archived-ancestor fetch composition |
| bd-3uzt ENVELOPE-RESOLUTION | **PASS** | `ResolveEnvelope` + partition; TC-BUDGET-ENVELOPE-* in `BudgetEnvelopeResolutionTests` |
| bd-113k ENVELOPE-INTEGRITY | **PASS** | Ancestry integrity, overflow, refund sign, archived ancestor in `BudgetEnvelopeIntegrityTests` |
| bd-zsby GATE-INT-ENVELOPE-PROVENANCE | **PASS** | `BudgetEnvelopeProvenanceTests` (depth-3 effective category, null effective, reparent re-lens); UC003 31 green; position 111 green |

Refs footers verified: `bd-1xlf`→`43bab99`, `bd-tuey`→`9906b8f`, `bd-3uzt`→`d65b52f`, `bd-113k`→`5db9b26`, `bd-zsby`→`f7203c3`.

### Rest of plan (re-confirmation)

All remaining 24 task beads remain closed; graph coverage 11/11 FRs, 0 orphans; path-check healthy 39/39; four external deps validated. Prior residual structural beads from 2026-07-28:

| Bead | Status |
|---|---|
| bd-2zge, bd-b5fl, bd-27ye, bd-nqp9, bd-fxa3, bd-2vne | **closed** (addressed post prior review) |
| bd-12td | **open** — restore original 3s/1s p95 ambition (explicit non-goal for this review) |

## Phase 3 — Code quality (envelope)

### Fixed this review (MECHANICAL)

| Finding | Fix |
|---|---|
| `AbsorbedCategoryIds` followed membership **list order**, not ascending **Ordinal** first-seen (comment/DD claimed ordinal) | Process `actualMembers.OrderBy(m => m.Ordinal)` before aggregation; reverse-list unit test |

### Residual WARNs (not must-fix)

| Finding | Severity | Disposition |
|---|---|---|
| `Property_partition_reconciles_across_generated_envelope_shapes` is multi-shape smoke, not generative trees | WARN / TEST_QUALITY | Residual — exactly-once still proven by partition + membership totals; generative coverage optional follow-up |
| Process handlers drop calculator integrity reason tokens (map only to `BUDGET-INTEGRITY`) | WARN | Fail-closed is correct; diagnostics only. Partial history in bd-nqp9 |
| Insights-only `EffectiveCategoryId` | — | **Not a finding** — by DD design (`BudgetActualMember` is insights surface) |
| 7 DDs with `no_explicit_file_contract` on anchor reconcile | WARN / UPSTREAM_DOC | Pre-existing; not guessed |

### Strengths

1. **Exactly-once spine** — checked membership sum, exclusive nearest-ancestor assignment, bucket reconciliation in `BudgetPositionCalculator`.
2. **Zero-child blocks parent** — explicit zero entry terminates scan; funded ancestor Remaining unreduced (resolution tests).
3. **Published-process reparent proof** — same revision, new snapshot, re-attributed absorption via `BudgetEnvelopeProvenanceTests`.
4. **Fast verification tier** — `scripts/verify-budget-fast.sh` keeps edit-cycle gates under 60s; module script is ship-only.

## Phase 4 — Classification

| Item | Class | Action |
|---|---|---|
| Absorbed ordinal order | MECHANICAL | **Fixed** |
| Generative exactly-once property | AMBIGUOUS / optional | Residual WARN (no bead filed — low blast radius) |
| Integrity token on process surface | AMBIGUOUS product | Residual WARN |
| Anchor unresolved DDs | UPSTREAM_DOC | Reported; no path invention |

## Phase 5 — Iterate

1. Fixed ordinal absorption + test.  
2. Re-ran envelope suites (65 green) + fast gate.  
3. No further FAILs.

Iterations used: **1** (of 3).

## Phase 5b — Anchor reconcile

```text
lex anchor reconcile --module BUDGET --plan PLAN-BUDGET-V1 --apply --json
```

| Metric | Count |
|---:|
| Created (envelope apply earlier) | 18 |
| Refreshed | 17 |
| Existing (stable re-run) | 113 |
| **Unresolved** | **7** |

Unresolved subjects (`no_explicit_file_contract`):

- DD-BUDGET-CLI-OPERATION-CONTRACT  
- DD-BUDGET-EXACT-POSITION-CALCULATION  
- DD-BUDGET-IDEMPOTENT-MUTATIONS  
- DD-BUDGET-INSIGHTS-READ-PROJECTION  
- DD-BUDGET-PLAN-REVISION-LIFECYCLE  
- DD-BUDGET-STATE-STORE  
- DD-BUDGET-TRUSTED-PERIOD-TIME  

**Proof harvest (Phase 6) not re-run this session** — skill forbids harvest when unresolved anchors remain non-zero without inventing paths. Prior harvest at `aba0d54` still covers baseline FRs/NFRs; envelope TC links exist in coverage for FR-BUDGET-POSITION-QUERY / FR-BUDGET-INSIGHTS-PROJECTION. Recommend a dedicated follow-up to attach `expected_paths` (or task file contracts) to those seven DDs, then re-harvest + attest.

## Phase 7 — Compounding

- **Gate wall-clock lesson compounded already** via `verify-budget-fast.sh` + AGENTS tiers (module ≠ per-bead).  
- **Absorbed-order lesson:** when a DD says “ordinal order,” sort by ordinal explicitly — list order is not a substitute even if production currently emits dense ordered pages.

## Terminal receipt

| Item | Status |
|---|---|
| Spec conformance | **passed** |
| Code quality | **passed** |
| Open must-fix | **0** |
| Anchor reconcile unresolved | **7** (blocks fresh proof harvest + flow attest per skill) |
| `lex flow attest review-work` | **suppressed** (incomplete proof harvest boundary) |

### Actionable residual list

1. **Decision (optional):** attach file contracts / expected_paths to the seven unresolved DDs → re-run anchor reconcile → re-harvest FR proof for envelope ACs → attest.  
2. **Mechanical (optional):** generative exactly-once property test for envelope trees.  
3. **Ambition (open):** `bd-12td` restore 3s/1s p95 after catalogue-read dedup.

### Handoff

Envelope extension is review-clean for implementation quality and bead satisfaction. Plan archival attestation remains gated on anchor completeness + proof harvest, not on open code defects.
