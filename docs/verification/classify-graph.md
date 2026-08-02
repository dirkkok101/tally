# CLASSIFY graph and evidence quality

Status: verification gate for TASK-CLASSIFY-ERGONOMICS-GATE-MODULE /
PAT-CORE-IMPLEMENTATION-PLAN-QUALITY-GATES / bead bd-2u6r under
PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1 (also revalidates shipped PLAN-CLASSIFY-RULEBOOK-V1).

This report is **metadata-only**. It records graph commands, exact counts, graph ref-codes,
suite discovery floors, plan/bead inventory, and content fingerprints. It does **not** include
private fixture paths or content, descriptions, normalized tokens, amounts, expected corpus
rows, secrets, request/response JSON, or other financial/private payloads.

The report **must not embed its own raw self-hash**; live report hashing is emitted only by
scripts/verify-classify-graph.sh.

## Gate command

```bash
bash scripts/verify-classify-graph.sh
```

Expected: exit 0; coverage is 18 of 18 active FRs with at least 27 linked test cases and zero
coverage orphans; all design paths match (floor >= 30); link suggestions are empty; three CLI-only
endpoint heuristics are recorded as non-applicable; every named suite discovers tests; ergonomics
plan coverage/audit is gap-free; 105/17 inventory claim and five additive operations are present;
forbidden-surface and placeholder scans are empty.

## Evidence surface

| Artifact | Role |
|---|---|
| tests/Tally.Tests/Classify/ClassifyGraphEvidenceGuardTests.cs | Named-suite presence, plan/bead tracing, inventory, privacy, forbidden-surface guards |
| scripts/verify-classify-graph.sh | Graph, plan, discovery, and forbidden-surface runner |
| docs/verification/classify-graph.md | This metadata-only report |
| .lexicon/graph/CLASSIFY/module.json | Canonical module identity (read-only for this gate) |

## Inventory and operator ergonomics

| Claim | Expected |
|---|---|
| Global registry | 105 operations |
| CLASSIFY operations | 17 (released C12 + five additive) |
| Additive ops | classify.outcome.list, classify.rule.list, classify.rule-set.active.get, classify.corpus.build, classify.unresolved.report |
| Production-usable engine | Shipped CLASSIFY 0.3.3 C12 baseline remains authoritative |
| Operator ergonomics | Additive discovery/report/corpus surfaces on that baseline (no migration) |

## Plans, tasks, and beads

PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1 has 13 tasks. Bead inventory:

| `bd-1gly` | ergonomics plan bead |
| `bd-3k1z` | ergonomics plan bead |
| `bd-vg33` | ergonomics plan bead |
| `bd-rly1` | ergonomics plan bead |
| `bd-1cik` | ergonomics plan bead |
| `bd-29ch` | ergonomics plan bead |
| `bd-3mdk` | ergonomics plan bead |
| `bd-2vbg` | ergonomics plan bead |
| `bd-wsjo` | ergonomics plan bead |
| `bd-2byd` | ergonomics plan bead |
| `bd-elq8` | ergonomics plan bead |
| `bd-3ciw` | ergonomics plan bead |
| `bd-2u6r` | ergonomics plan bead |

| Task | Bead |
|---|---|
| TASK-CLASSIFY-ERGONOMICS-CONTRACT-FOUNDATION | bd-1gly |
| TASK-CLASSIFY-ERGONOMICS-CORPUS-MAPPER | bd-3k1z |
| TASK-CLASSIFY-ERGONOMICS-OUTCOME-LIST | bd-vg33 |
| TASK-CLASSIFY-ERGONOMICS-RUNTIME-CONVERGENCE | bd-rly1 |
| TASK-CLASSIFY-ERGONOMICS-CORPUS-BUILDER | bd-1cik |
| TASK-CLASSIFY-ERGONOMICS-CURSOR-POLICY | bd-29ch |
| TASK-CLASSIFY-ERGONOMICS-PRIVACY-RECOVERY-GATE | bd-3mdk |
| TASK-CLASSIFY-ERGONOMICS-RULE-DISCOVERY | bd-2vbg |
| TASK-CLASSIFY-ERGONOMICS-BULK-PREVIEW-COMPOSITION | bd-wsjo |
| TASK-CLASSIFY-ERGONOMICS-PROCESS-THROUGHPUT-GATE | bd-2byd |
| TASK-CLASSIFY-ERGONOMICS-UNRESOLVED-POLICY | bd-elq8 |
| TASK-CLASSIFY-ERGONOMICS-UNRESOLVED-REPORT | bd-3ciw |
| TASK-CLASSIFY-ERGONOMICS-GATE-MODULE | bd-2u6r |

PLAN-CLASSIFY-RULEBOOK-V1 remains the historical 30-task shipped baseline.

## Graph commands and expected counts

| Command | Expected |
|---|---|
| lex coverage --module CLASSIFY --json | Status healthy; 18/18 active FRs; >=27 linked TCs; 0 orphans |
| lex decision path-check --module CLASSIFY --json | healthy; matched=total; missing=0; total>=30 |
| lex link suggest --module CLASSIFY --json | Empty |
| lex plan coverage PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1 --json | gap_count=0 |
| lex plan audit PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1 --json | blocking_finding_count=0 |
| lex plan status PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1 --json | ready; 13 tasks |
| lex plan coverage PLAN-CLASSIFY-RULEBOOK-V1 --json | historical baseline (residual additive gaps tolerated) |
| lex plan audit PLAN-CLASSIFY-RULEBOOK-V1 --json | blocking_finding_count=0 |
| lex plan status PLAN-CLASSIFY-RULEBOOK-V1 --json | ready; 30 tasks |

## Governing decisions / FRs (ergonomics)

Decisions: DD-CLASSIFY-OPERATOR-ERGONOMICS-CONTRACT, DD-CLASSIFY-SHIPPED-BASELINE,
DD-CLASSIFY-PAGINATED-DISCOVERY, DD-CLASSIFY-PRIVATE-CORPUS-PUBLICATION,
DD-CLASSIFY-UNRESOLVED-REPORT-BOUNDARY.

FRs: FR-CLASSIFY-OUTCOME-DISCOVERY, FR-CLASSIFY-RULEBOOK-DISCOVERY,
FR-CLASSIFY-PRIVATE-CORPUS-BUILDER, FR-CLASSIFY-UNRESOLVED-PATTERN-REPORT,
FR-CLASSIFY-BULK-PREVIEW-COMPOSITION.

## Content fingerprints (immutable inputs; raw SHA-256)

This report path (docs/verification/classify-graph.md) is excluded from the raw hash table:
it is the artifact being written, so a pre-write hash cannot match final bytes. The report
must not embed its own raw self-hash. Live report hash is emitted by
scripts/verify-classify-graph.sh only.

| Artifact | SHA-256 | Bytes |
|---|---|---:|
| `scripts/verify-classify-graph.sh` | `de53f6a420b85bf17fa20696ec90dc674d6f63ca106f2600cb9e556fdd5808b3` | 32668 |
| `tests/Tally.Tests/Classify/ClassifyGraphEvidenceGuardTests.cs` | `c1783c84791b486615b0c061f5d6b6de53b6f70dd3a4bfa6449b9dfc5b420b97` | 23881 |
| `.lexicon/graph/CLASSIFY/module.json` | `9f10ec81f359bc1d2d164c4fd58588a1ab80ab9ef26ab8a117bdd86ff558a478` | 7918 |

## Result

Never open or mutate live data. ClassifyGraphQualityEvidence is current when
bash scripts/verify-classify-graph.sh exits 0.
