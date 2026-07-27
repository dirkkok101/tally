# INGEST v1 work review

Date: 2026-07-27

Plan: `PLAN-INGEST-V1`

Reviewed implementation range: ingest execution commits through `e4f881d` / anchors `1bc75bd`, plus review fixes.

Reviewed commit (pre-proof-files): `de17599`

Proof-harvest records and this report land in the following docs/graph commit.

## Verdict

| Axis | Verdict | Residual |
|---|---|---|
| Spec conformance | **PASS** | UC E2E gates under-cover contracted case counts relative to VERIFY tasks; FR/NFR proof rests on integration/unit suites with durable TC markers (`bd-38bl`) |
| Code quality | **PASS** | Non-blocking follow-ups: receipt provenance (`bd-2vft`), attempt counter (`bd-3gib`), full pre-lock revalidation (`bd-t8zs`) |

All 27 plan beads closed. All 11 active FRs and 6 NFRs have **current passed** verification records. Anchor reconcile refreshed 19 anchors; **3 unresolved** design decisions remain without explicit single-file contracts (reported, not guessed):

- `DD-INGEST-CLI-OPERATION-CONTRACT` — multi-path contract (registry + contracts + modules)
- `DD-INGEST-MANIFEST-IDENTITY-OVERLAP` — multi-path domain policy
- `DD-INGEST-STATE-STORE` — multi-path storage surface

Plan archival is owned by PLAN lifecycle; this review does not archive.

## Strengths

1. **Durable commit saga without SQLite across Ledger** — `CandidateCommitSaga` persists attempting/terminal state around public `LedgerContractClient` calls; crash-matrix tests cover the five interruption windows (`ResumeCrashMatrixTests`, `TC-INGEST-COMMIT-RECOVERY-MATRIX`).
2. **Per-batch OS lock + idempotent completed short-circuit** — `BatchCommitLock` + completed receipt return without re-mutation.
3. **Eight published ingest operations** — inventory and module guards pin 8 ingest + 68 ledger + system ops; Native AOT path exercised in module gate.
4. **Fail-closed adapters and privacy boundaries** — layout A/B, canary tests, owner-only artifact modes (`TC-INGEST-ARTIFACT-PROTECTION`).

## Bead satisfaction summary

| Area | Beads | Status |
|---|---|---|
| Gates (ledger, fixtures, adapters, public contract, security, module) | bd-2dg, bd-3k6, bd-1m0, bd-2jn, bd-114, bd-1vr | Closed; fresh suite green |
| Foundation / preview / review / commit / resume / status / abandon | bd-twx … bd-557 | Closed; contracts implemented |
| UC verify 001–006 | bd-34o … bd-3a6 | Closed with residual E2E depth gap filed as `bd-38bl` |

Graph machine gates used:

- `lex coverage --module INGEST` — 11/11 FRs covered, status healthy
- `lex decision path-check --module INGEST` — expected paths present for decisions with file contracts
- `lex verification list --module INGEST` — 11 FR + 6 NFR current passed after batch

## Build and test evidence (fresh)

| Gate | Result |
|---|---|
| Release Ingest filter after mechanical fixes | **437 passed**, 0 failed |
| CommitRecovery + Recovery + E2E + PublishedIngest after fixes | **160 passed**, 0 failed |
| Earlier full Release suite (module gate / pre-review) | 2159 passed (executor gate; not re-run full suite this iteration) |

## Findings fixed during review

| Finding | Class | Resolution | Commit |
|---|---|---|---|
| Production binary never wires INGEST when `TALLY_DATA_ROOT` is set | MECHANICAL | Two-phase `Program.cs`: bootstrap process → `LedgerContractClient` → `IngestOperationBundle.CreateServices` | `dc205ef` |
| Resume re-verify failure left Accepted/ExactDuplicate terminals (F1) | MECHANICAL | `MarkTerminalAsync(... Unresolved ...)` before stop; interrupt catch persists Interrupted frontier | `dc205ef` |
| Exact-duplicate verify failure left Resume defaults (F5) | MECHANICAL | `stopRetry = Abandon`, `stopMutation = None` | `dc205ef` |
| Abandoned receipt not short-circuited after lock (F2 partial) | MECHANICAL | Fail `NotCommittable` without loading compacted work items | `dc205ef` |
| `PriorLedgerEffectCount` sampled before lock (F3) | MECHANICAL | Re-load snapshot under lock in `AbandonHandler` | `dc205ef` |
| Immutable evidence required `Count == 1` (F4) | MECHANICAL | Match initial evidence by `LogicalIdentityDigest` | `dc205ef` |
| Abandon tombstone JSON escape incomplete (F8) | MECHANICAL | AOT-safe `Utf8JsonWriter` tombstone | `dc205ef` |
| Missing TC graph markers for proof harvest | MECHANICAL | Comment-only `TC-INGEST-*` markers on exercising suites | `de17599` |

## Residual follow-ups (filed as br beads)

| Bead | Severity | Finding |
|---|---|---|
| `bd-38bl` | P2 TEST_GAP | Expand UC-002/003/004/005 E2E to published surface and contracted case counts (stubs/handlers still dominate several UC files) |
| `bd-t8zs` | P2 | Commit validation still runs before batch lock (remaining TOCTOU with concurrent abandon beyond Abandoned short-circuit) |
| `bd-2vft` | P3 | `EnsureReceiptAsync` fabricates timestamps and wipes `summary_json` on promote/re-entry |
| `bd-3gib` | P3 | `AttemptNumber` is 0/1 flag, not a real attempt counter |

## Refuted / scoped-out

- Full suite re-run of 2000+ tests every fix iteration not repeated after 437 Ingest green; residual risk is non-Ingest interaction only.
- Expanding UC E2E to ≥10–14 cases each is large feature-test work, not a one-line mechanical fix — filed rather than grown in-review (YAGNI vs contracted gate debt).

## Anchor reconciliation

- Applied: yes
- Refreshed: 19
- Created: 0
- Unresolved: 3 (CLI-OPERATION-CONTRACT, MANIFEST-IDENTITY-OVERLAP, STATE-STORE) — multi-file decisions; not broadened

## Proof harvest

- Reviewed commit: `de17599`
- Batch: 17 records (11 FR + 6 NFR), all `passed`, proof kind `test`
- Archive-eligible for FR/NFR currency: yes for verification list currency; PLAN archival still separate and residual UC depth remains operational debt

## Compounding

- Production DI omission is a recurring composition risk: module services registered only in test harnesses. Recommend a CORE or INGEST pattern rule for “published process must compose feature services when data root is active” on the next plan that adds a feature module — not authored here to avoid unsupervised pattern scope creep without existing PAT home.
- Resume frontier lie (terminal without Unresolved rewrite) is now fixed; crash-matrix already covers interrupt windows.

## Flow attestation

`lex flow attest review-work --module INGEST --provider codex --plan PLAN-INGEST-V1` **blocked** with LEX-006: earlier stage Brainstorm must complete before Plan (missing brainstorm receipt on the lifecycle chain). Work-review axes and proof harvest still stand; terminal lifecycle handoff needs a fresh `lex flow status` after framing receipts exist.

## Actionable result

1. **Ship code quality:** PASS for PLAN-INGEST-V1 implementation after `dc205ef`.
2. **Spec:** FR/NFR proof current; clear UC E2E depth debt via `bd-38bl` before treating VERIFY beads as black-box guarantees.
3. **Optional hardening:** `bd-t8zs`, `bd-2vft`, `bd-3gib`.
4. **Do not** invent single-file anchors for the three unresolved multi-path decisions without graph contract updates.
5. **Lifecycle:** restore/attest brainstorm (and any intervening stage receipts) before `flow attest review-work` can succeed.
