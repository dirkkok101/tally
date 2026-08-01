# CLASSIFY graph and evidence quality

Status: verification gate for `TASK-CLASSIFY-RULEBOOK-GATE-GRAPH-QUALITY` /
`PAT-CORE-IMPLEMENTATION-PLAN-QUALITY-GATES` / bead `bd-1yaj`.

This report is **metadata-only**. It records graph commands, exact counts, graph ref-codes,
suite discovery floors, and content fingerprints. It does **not** include private fixture
paths or content, descriptions, normalized tokens, amounts, expected corpus rows, secrets,
request/response JSON, or other financial/private payloads.

## Gate command

```bash
bash scripts/verify-classify-graph.sh
```

Expected: exit 0; coverage is 13 of 13 active FRs with at least 20 linked test cases and zero
gaps/orphans; all design paths match (floor ≥ 30); link suggestions are empty; three CLI-only
endpoint heuristics are recorded as non-applicable; every named suite discovers tests; plan
coverage/audit/context/dependency checks are clean; forbidden-surface and placeholder scans are
empty.

## Evidence surface

| Artifact | Role |
|---|---|
| `tests/Tally.Tests/Classify/ClassifyGraphEvidenceGuardTests.cs` | Named-suite presence + forbidden-surface + placeholder guards (`ClassifyGraphQualityEvidence`) |
| `scripts/verify-classify-graph.sh` | Graph, plan, discovery, and forbidden-surface runner |
| `docs/verification/classify-graph.md` | This metadata-only report |
| `.lexicon/graph/CLASSIFY/module.json` | Canonical module identity (read-only for this gate) |

## Graph commands and expected counts

| Command | Expected |
|---|---|
| `lex coverage --module CLASSIFY --json` | Status `healthy`; 13/13 active FRs covered; ≥20 unique linked TCs; 0 orphans; 0 gaps; 0 warnings |
| `lex test-case list --module CLASSIFY --json` | Inventory equals unique linked TC count |
| `lex decision path-check --module CLASSIFY --json` | Status `healthy`; matched = total; `missing_count=0`; total ≥ 30 |
| `lex link suggest --module CLASSIFY --json` | Empty list |
| `lex endpoint suggest --module CLASSIFY --json` | Exactly 3 heuristics (below), recorded as non-applicable |
| `lex endpoint list --module CLASSIFY --json` | Empty (no HTTP endpoint entities) |
| `lex external-dependency check --module CLASSIFY --json` | Four deps with linked TC evidence |
| `lex plan coverage PLAN-CLASSIFY-RULEBOOK-V1 --json` | `gap_count=0` |
| `lex plan audit PLAN-CLASSIFY-RULEBOOK-V1 --json` | `blocking_finding_count=0` |
| `lex plan status PLAN-CLASSIFY-RULEBOOK-V1 --json` | `planning_state=ready`; 30 tasks |
| `lex context <TASK> --max-tokens 2500 --json` | All 30 plan tasks core sections within 2500 tokens |

## Endpoint heuristics (CLI-only, non-applicable)

The accepted design is **local structured CLI** with zero Lex endpoint entities. The three known
heuristics must remain present and must **not** be silenced by adding HTTP:

| Rule | Source FR | Disposition |
|---|---|---|
| `detail-flow` | `FR-CLASSIFY-CONTRACT-DISCOVERY` | Non-applicable — discovery is a CLI schema operation, not GET item-detail |
| `management-write-flow` | `FR-CLASSIFY-RULE-LIFECYCLE` | Non-applicable — rule lifecycle is typed CLI mutation, not POST |
| `search-flow` | `FR-CLASSIFY-STATUS-HISTORY` | Non-applicable — status is a CLI query, not HTTP search |

## External dependencies (evidence-linked)

| Ref | Linked test cases (examples) | Recorded status |
|---|---|---|
| `EXT-CLASSIFY-LEDGER-PUBLIC-CONTRACT` | `TC-CLASSIFY-ELIGIBLE-PROJECTION-CONTRACT`, `TC-CLASSIFY-APPLY-EXECUTION-CONTRACT` | evidence-linked |
| `EXT-CLASSIFY-HOST-OS-SECURITY` | `TC-CLASSIFY-LOCAL-ARTIFACT-PROTECTION`, `TC-CLASSIFY-STRUCTURED-INVOCATION-CONTRACT` | evidence-linked |
| `EXT-CLASSIFY-AI-AGENT-HOST` | `TC-CLASSIFY-CONTRACT-DISCOVERY-CONTRACT` | evidence-linked |
| `EXT-CLASSIFY-PRIVATE-EVALUATION-CORPUS` | `TC-CLASSIFY-RULE-VALIDATION-CONTRACT` | evidence-linked |

Final module gate (`bd-3l4k`) owns any additional consumer pairing; this gate does not force-validate without evidence.

## Named suites (nonzero discovery required)

Per-class discovery must meet its floor (≥1 for feature suites; higher floors for UC, security,
private-evidence, and contract suites) **before** aggregate CLASSIFY totals are accepted:

| Family | Classes |
|---|---|
| Feature — evaluation | `ClassificationDeterminismPropertyTests`, `ClassificationEngineTests`, `ClassificationEvaluationInput*`, `EvaluateClassificationCommandTests`, `EvaluationLimitTests`, `EvaluationPersistenceTests`, `OutcomeExplanationTests`, `OutcomeInvalidationTests` |
| Feature — rules | `ClassificationRuleVocabularyTests`, `NormalizerV1Tests`, `RuleActivationTests`, `RuleDraftPersistenceTests`, `RuleRetirementTests`, `SaveClassificationRuleTests` |
| Feature — apply | `ApplyAuthorizationTests`, `ApplyPreviewTests`, `ClassificationApplySagaTests`, `ClassificationApplyCrashRecoveryTests` |
| Feature — feedback / recovery | `ClassificationFeedbackTests`, `FeedbackProposalTests`, `AbandonCleanupTests`, `ClassificationStatusTests`, `StatusPrivacyTests` |
| Storage | `ClassifyHistoryInvariantTests`, `ClassifyStateStoreTests` |
| Contract / process | `ClassifyOperationContractTests`, `ClassifyPublishedContractTests`, `ClassifyProcessContractTests` |
| Integration (LEDGER) | `ClassifyLedgerBoundaryArchitectureTests`, `ClassifyLedgerContractClientTests`, `LedgerClassification*`, `LedgerClassifyPrerequisiteTests` |
| Security | `ClassifyArtifactProtectionTests`, `ClassifySecurityGateTests` |
| Private-evidence / validation | `OwnerRulebookGateTests`, `ClassificationRuleValidationTests`, `PrivateCorpus*`, `ValidationLimitTests`, `ValidationPrivacyTests` |
| UC | `ClassifyUc001EvaluationTests` … `ClassifyUc006AgentContractTests` |
| Graph gate | `ClassifyGraphEvidenceGuardTests` |

## Forbidden surfaces and placeholders

Scans over CLASSIFY `src` and `tests` must find **zero**:

- HTTP / FastEndpoints / AspNetCore / `HttpClient` / listeners
- EF / `DbContext` / Npgsql
- Hosted services / plugin loaders
- `TODO` / `FIXME` / `HACK` / `XXX` / `NotImplementedException`

## Plan quality

- Coverage: all required refs covered; gate/validation tasks may remain intentionally loose (no `implements`)
- Audit: zero blocking findings (informational optional-generic-ref notes allowed)
- Dependencies among `TASK-CLASSIFY-RULEBOOK-*` are acyclic
- Context budgets: core recipe sections ≤ 2500 tokens per plan task

## How to re-run

```bash
dotnet build Tally.slnx -c Release --nologo
lex coverage --module CLASSIFY --json | jq '.Summary'
lex decision path-check --module CLASSIFY --json | jq '{status, missing_count}'
bash scripts/verify-classify-graph.sh
```

## Result

Record the runner exit code, FR/TC/path counts, per-class discovery counts, endpoint-heuristic
disposition, external-dep statuses, and content fingerprints when the gate is executed. Do not
paste private fixtures, descriptions, tokens, amounts, or financial payloads.

## Latest run

Executed on 2026-08-01 via `bash scripts/verify-classify-graph.sh` for `bd-1yaj`.

| Check | Result |
|---|---|
| `lex coverage` | 13/13 FRs; 21 unique linked TCs (≥20 floor); 0 orphans; healthy |
| `lex decision path-check` | 35/35 paths matched; healthy (≥30 floor) |
| `lex link suggest` | 0 suggestions |
| `lex endpoint suggest` | 3 heuristics → non-applicable CLI-only |
| `lex endpoint list` | 0 entities |
| External deps | 4 evidence-linked |
| Plan coverage / audit | 174/174 covered; 0 gaps; 0 blocking findings |
| Context budgets | 30/30 core sections ≤ 2500 tokens |
| Dependencies | Acyclic (30 rulebook tasks, 97 edges) |
| Named suite discovery | 49 classes, each ≥ floor (aggregate CLASSIFY discovery 1019 — not sole evidence) |
| Guard tests | 8/8 passed |
| Forbidden / placeholder scans | 0 hits |
| Script exit | 0 |

### Content fingerprints (metadata)

| Artifact | SHA-256 | Bytes |
|---|---|---:|
| `scripts/verify-classify-graph.sh` | `d01441fce4a47b28d9cb573dcbfead27cf5604ccced16c1ea305e8bf149dba8f` | 24107 |
| `docs/verification/classify-graph.md` | `41f892f3b03f05c09555e4472bf1aad31054057caeb6357d5e7a991b0b0828fa` | 8300 |
| `tests/Tally.Tests/Classify/ClassifyGraphEvidenceGuardTests.cs` | `037bfcf9e5276ff0de7fd94def657ff8b15f37c2e18b997a8991cabb6b3855ed` | 13052 |
| `.lexicon/graph/CLASSIFY/module.json` | `3975f321fe72cc7f86c82934af1c0be8add2e6fbe9dc444750d7b7ce42f8df91` | 18902 |
