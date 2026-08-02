# CLASSIFY v1 verification

Status: **passed** on 2026-08-02 (commit `f50f53c` / `f50f53cb6a691b5b2af15743dfee4e1d1ef2a71a`).

The CLASSIFY completion gate is executed by `bash scripts/verify-classify-module.sh`.
The script requires Release restore/build, CLASSIFY-owned format verification, the complete
full test suite, linux-x64 Native-AOT publish, current non-stale ClassifyGraphQualityEvidence,
graph/path/dependency evidence, nonzero named CLASSIFY suites, evidence-bound external
dependency statuses, kill-criterion clearance, and clean git whitespace.

This report is **metadata-only**. It must not contain private fixture paths or content,
descriptions, normalized tokens, amounts, expected corpus rows, secrets, or financial payloads.

## Gate command

```bash
bash scripts/verify-classify-module.sh
```

Expected: exit 0; nonzero named-suite discovery; full suite 0 failures; four external
dependencies `validated`; five kill criteria `clear`.

## Latest run

| Gate | Result |
|---|---|
| Host | kernel=Linux 7.0.0-28-generic; cpus=16; load=9.11 7.00 4.99 |
| Tools | lex=0.5.12; dotnet=10.0.110 |
| Commit | `f50f53cb6a691b5b2af15743dfee4e1d1ef2a71a` |
| `dotnet restore Tally.slnx` | executed |
| `dotnet build Tally.slnx -c Release` | zero-warning (TreatWarningsAsErrors) |
| `dotnet format` (CLASSIFY-owned paths) | verify-no-changes |
| Native-AOT `linux-x64` publish | executable present (temp publish root); 0 trim/reflection/dynamic-code warning markers scanned |
| Named CLASSIFY suite discovery | 50 classes; each ≥ floor; aggregate CLASSIFY discovery=1029 |
| `ClassifyModuleGuardTests` | executed under Release |
| Complete full test suite | passed=5609 failed=0 skipped=0 total=5609 |
| `scripts/verify-classify-graph.sh` | invoked (non-stale ClassifyGraphQualityEvidence) |
| `scripts/verify-classify-contract.sh` | invoked |
| `scripts/verify-classify-security.sh` | invoked |
| Owner-rulebook specialized gate | env-gated (path never printed); in-suite OwnerRulebookGateTests always run |
| `lex check --fast` | executed |
| `lex coverage --module CLASSIFY` | 13/13 healthy; 0 orphans |
| `lex decision path-check` | 35/35 matched; healthy |
| `lex link suggest` | 0 |
| `lex plan coverage PLAN-CLASSIFY-RULEBOOK-V1` | gap_count=0 |
| `lex plan audit PLAN-CLASSIFY-RULEBOOK-V1` | blocking_finding_count=0 |
| Kill criteria | 5/5 `clear` |
| External dependencies | 4/4 `validated` (evidence-bound; left unchanged when already truthful) |
| `git diff --check` | executed |
| Module script fail_count | 0 |

## External dependency statuses

| Ref | Status | Named evidence (metadata) |
|---|---|---|
| `EXT-CLASSIFY-LEDGER-PUBLIC-CONTRACT` | validated | LEDGER projection/mutation prerequisite + apply suites |
| `EXT-CLASSIFY-AI-AGENT-HOST` | validated | UC-006 agent contract + published discovery/invocation |
| `EXT-CLASSIFY-HOST-OS-SECURITY` | validated | Security gate + owner-only modes / offline isolation |
| `EXT-CLASSIFY-PRIVATE-EVALUATION-CORPUS` | validated | Owner-rulebook + private corpus reader/validation suites |

## Kill criteria

| Id | State |
|---|---|
| `01KXV5M9XWRQGW8RATSM9G93NB` | clear |
| `01KXV5MA8B6MMYSRPHSQ07FDR0` | clear |
| `01KXV5MAKA79V0ZG099TWWD5FE` | clear |
| `01KXV5MAXN2TPCA3BCGP6W6AJM` | clear |
| `01KYVME50DZPAMGR2G3XVS8C5W` | clear |

## Named CLASSIFY suites (nonzero discovery required)

- `ClassificationDeterminismPropertyTests` — discovery 30
- `ClassificationEngineTests` — discovery 32
- `ClassificationEvaluationInputCancellationTests` — discovery 6
- `ClassificationEvaluationInputLoaderTests` — discovery 26
- `EvaluateClassificationCommandTests` — discovery 13
- `EvaluationLimitTests` — discovery 9
- `EvaluationPersistenceTests` — discovery 10
- `OutcomeExplanationTests` — discovery 13
- `OutcomeInvalidationTests` — discovery 25
- `ClassificationRuleVocabularyTests` — discovery 27
- `NormalizerV1Tests` — discovery 14
- `RuleActivationTests` — discovery 25
- `RuleDraftPersistenceTests` — discovery 8
- `RuleRetirementTests` — discovery 13
- `SaveClassificationRuleTests` — discovery 25
- `ApplyAuthorizationTests` — discovery 36
- `ApplyPreviewTests` — discovery 25
- `ClassificationApplySagaTests` — discovery 28
- `ClassificationApplyCrashRecoveryTests` — discovery 16
- `ClassificationFeedbackTests` — discovery 14
- `FeedbackProposalTests` — discovery 24
- `AbandonCleanupTests` — discovery 32
- `ClassificationStatusTests` — discovery 38
- `StatusPrivacyTests` — discovery 12
- `ClassifyHistoryInvariantTests` — discovery 12
- `ClassifyStateStoreTests` — discovery 24
- `ClassifyOperationContractTests` — discovery 59
- `ClassifyPublishedContractTests` — discovery 22
- `ClassifyProcessContractTests` — discovery 38
- `ClassifyLedgerBoundaryArchitectureTests` — discovery 5
- `ClassifyLedgerContractClientTests` — discovery 16
- `LedgerClassificationMutationPreconditionTests` — discovery 15
- `LedgerClassificationProjectionTests` — discovery 18
- `LedgerClassifyPrerequisiteTests` — discovery 25
- `ClassifyArtifactProtectionTests` — discovery 24
- `ClassifySecurityGateTests` — discovery 31
- `OwnerRulebookGateTests` — discovery 20
- `ClassificationRuleValidationTests` — discovery 23
- `PrivateCorpusPrivacyTests` — discovery 9
- `PrivateCorpusReaderTests` — discovery 25
- `ValidationLimitTests` — discovery 7
- `ValidationPrivacyTests` — discovery 4
- `ClassifyUc001EvaluationTests` — discovery 14
- `ClassifyUc002OutcomeTests` — discovery 34
- `ClassifyUc003ApplyTests` — discovery 23
- `ClassifyUc004RulesTests` — discovery 23
- `ClassifyUc005FeedbackTests` — discovery 15
- `ClassifyUc006AgentContractTests` — discovery 24
- `ClassifyGraphEvidenceGuardTests` — discovery 9
- `ClassifyModuleGuardTests` — discovery 9

## Content fingerprints (live at report write; raw SHA-256)

| Artifact | SHA-256 | Bytes |
|---|---|---:|
| `scripts/verify-classify-module.sh` | `6576de31f5e7e838c21340049da00387cc9d6816d8806a1a81b39d68464b14c2` | 28653 |
| `scripts/verify-classify-graph.sh` | `577bebaa02a225ebbc5821a50a6ecf8d333ace9ec9d1f1fe73152acc8f3919f3` | 26821 |
| `scripts/verify-classify-contract.sh` | `dd8bca2ade757964b85eae2b3a291c5cabf4e2dc744474bdfaa7ea5142e6e96d` | 3819 |
| `scripts/verify-classify-security.sh` | `e2ca022dd0f6d81e3f9c1c70af62a7cba549b704b8cc1cb7506451a91daffd39` | 12444 |
| `tests/Tally.Tests/Classify/ClassifyModuleGuardTests.cs` | `6bd3619dce3bfa9fe388b912708e814b0406f110f290b7e9f83489b4a6694640` | 12280 |
| `tests/Tally.Tests/Classify/ClassifyGraphEvidenceGuardTests.cs` | `3b39a93759d7c8582ff37785f524f811cee8fbd4fcdab5b125b9668b5fd0c1c8` | 16167 |
| `docs/verification/classify-v1.md` | `9767ff5344183ba9ca75224cda47790444c93b5064c0228be2a4ef0683570fed` | 8343 |
| `docs/verification/classify-graph.md` | `7b16a90ff89b5212d64da4d04312e7cf4361a7769f7a3e7465678c1ff1ddf6ce` | 9102 |
| `.lexicon/graph/CLASSIFY/module.json` | `3975f321fe72cc7f86c82934af1c0be8add2e6fbe9dc444750d7b7ce42f8df91` | 18902 |
| `.lexicon/graph/CLASSIFY/external-dependency/EXT-CLASSIFY-LEDGER-PUBLIC-CONTRACT.json` | `6a1dfd30eff93113657f2a68609c439d654e92bef1d614578e14737da7792882` | 2406 |
| `.lexicon/graph/CLASSIFY/external-dependency/EXT-CLASSIFY-AI-AGENT-HOST.json` | `e6b24f6d393aa97d4a2d5d016c3f661085e211225017e181bf7cc362234916b9` | 1071 |
| `.lexicon/graph/CLASSIFY/external-dependency/EXT-CLASSIFY-HOST-OS-SECURITY.json` | `374020520bc1c5d4f067cdd086477e1a372e64a403fc865042db428a462f33e8` | 937 |
| `.lexicon/graph/CLASSIFY/external-dependency/EXT-CLASSIFY-PRIVATE-EVALUATION-CORPUS.json` | `0088fb75dff5518590e1d1d96afb85e86cab4b873a3049cf4a7ceb610641e29b` | 1055 |

## How to re-run

```bash
dotnet restore Tally.slnx
bash scripts/verify-classify-module.sh
```

Specialized isolated gates remain available:

```bash
bash scripts/verify-classify-graph.sh
bash scripts/verify-classify-contract.sh
bash scripts/verify-classify-security.sh
# optional private operator gate (never print CLASSIFY_OWNER_RULEBOOK_CORPUS):
# bash scripts/verify-classify-owner-rulebook.sh
```

## Result

Record the runner exit code, suite counts, dependency statuses, kill checks, fingerprints,
and commit IDs. Do not paste private fixtures, paths, descriptions, tokens, amounts, or
financial payloads.

**VerifiedClassifyV1Module:** passed
