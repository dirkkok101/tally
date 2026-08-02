# CLASSIFY v1 verification

Status: **passed** on 2026-08-02 (commit `6537f5e` / `6537f5e1af41761bf9881474814593ac3defd9ea`).

The CLASSIFY completion gate is executed by `bash scripts/verify-classify-module.sh`.
The script requires Release restore/build, CLASSIFY-owned format verification, the complete
full test suite, linux-x64 Native-AOT publish, current non-stale ClassifyGraphQualityEvidence,
graph/path/dependency evidence, operator-ergonomics security/process gates, nonzero named
CLASSIFY suites, 105/17 inventory, frozen 0.3.3 fingerprints, evidence-bound external
dependency statuses, kill-criterion clearance, and clean git whitespace.

This gate closes the **operator ergonomics** increment on top of the **production-usable**
CLASSIFY engine baseline (shipped 0.3.3 C12). Migration: none. Release/install/tag remain
a separate authorized workflow.

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
| Host | kernel=Linux 7.0.0-28-generic; cpus=16; load=2.39 2.42 2.65 |
| Tools | lex=0.5.12; dotnet=10.0.110 |
| Commit | `6537f5e1af41761bf9881474814593ac3defd9ea` |
| `dotnet restore Tally.slnx` | executed |
| `dotnet build Tally.slnx -c Release` | zero-warning (TreatWarningsAsErrors) |
| `dotnet format` (CLASSIFY-owned paths) | verify-no-changes |
| Native-AOT `linux-x64` publish | executable present (temp publish root); 0 trim/reflection/dynamic-code warning markers scanned |
| Named CLASSIFY suite discovery | 63 classes; each ≥ floor; aggregate CLASSIFY discovery=1739 |
| `ClassifyModuleGuardTests` | executed under Release |
| Complete full test suite | passed=6486 failed=0 skipped=0 total=6486 |
| `scripts/verify-classify-graph.sh` | invoked (non-stale ClassifyGraphQualityEvidence) |
| `scripts/verify-classify-contract.sh` | invoked |
| `scripts/verify-classify-security.sh` | invoked |
| Owner-rulebook specialized gate | env-gated (path never printed); in-suite OwnerRulebookGateTests always run |
| `lex check --fast` | executed |
| `lex coverage --module CLASSIFY` | 18/18 healthy; 0 orphans |
| `lex decision path-check` | 40/40 matched; healthy |
| `lex link suggest` | 0 |
| `lex plan coverage PLAN-CLASSIFY-RULEBOOK-V1` | gap_count=0 |
| `lex plan audit PLAN-CLASSIFY-RULEBOOK-V1` | blocking_finding_count=0 |
| `lex plan coverage PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1` | gap_count=0 |
| `lex plan audit PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1` | blocking_finding_count=0 |
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
- `OutcomeListTests` — discovery 26
- `OutcomeCursorStalenessTests` — discovery 22
- `ClassificationRuleVocabularyTests` — discovery 27
- `NormalizerV1Tests` — discovery 14
- `RuleActivationTests` — discovery 25
- `RuleDraftPersistenceTests` — discovery 8
- `RuleRetirementTests` — discovery 13
- `SaveClassificationRuleTests` — discovery 25
- `RuleDiscoveryTests` — discovery 41
- `ApplyAuthorizationTests` — discovery 36
- `ApplyPreviewTests` — discovery 26
- `ClassificationApplySagaTests` — discovery 28
- `ClassificationApplyCrashRecoveryTests` — discovery 16
- `ClassificationFeedbackTests` — discovery 14
- `FeedbackProposalTests` — discovery 24
- `AbandonCleanupTests` — discovery 32
- `ClassificationStatusTests` — discovery 38
- `StatusPrivacyTests` — discovery 12
- `ClassifyHistoryInvariantTests` — discovery 12
- `ClassifyStateStoreTests` — discovery 24
- `ClassifyOperationContractTests` — discovery 70
- `ClassifyPublishedContractTests` — discovery 40
- `ClassifyProcessContractTests` — discovery 262
- `ClassifyOperatorErgonomicsContractTests` — discovery 57
- `ClassifyOperatorErgonomicsSecurityTests` — discovery 32
- `ClassifyOperatorErgonomicsProcessTests` — discovery 13
- `ClassifyOperatorBatchPreviewTests` — discovery 19
- `ClassifyCursorCodecTests` — discovery 56
- `ClassifyLedgerBoundaryArchitectureTests` — discovery 5
- `ClassifyLedgerContractClientTests` — discovery 16
- `LedgerClassificationMutationPreconditionTests` — discovery 15
- `LedgerClassificationProjectionTests` — discovery 18
- `LedgerClassifyPrerequisiteTests` — discovery 25
- `ClassifyArtifactProtectionTests` — discovery 24
- `ClassifySecurityGateTests` — discovery 31
- `OwnerRulebookGateTests` — discovery 20
- `ClassificationRuleValidationTests` — discovery 24
- `ClassificationProjectionCorpusMapperTests` — discovery 33
- `PrivateCorpusPrivacyTests` — discovery 9
- `PrivateCorpusReaderTests` — discovery 25
- `PrivateCorpusBuilderTests` — discovery 38
- `PrivateCorpusWriterRecoveryTests` — discovery 32
- `ValidationLimitTests` — discovery 7
- `ValidationPrivacyTests` — discovery 4
- `UnresolvedPatternGroupingPolicyTests` — discovery 40
- `UnresolvedPatternReportTests` — discovery 40
- `ClassifyUc001EvaluationTests` — discovery 14
- `ClassifyUc002OutcomeTests` — discovery 34
- `ClassifyUc003ApplyTests` — discovery 23
- `ClassifyUc004RulesTests` — discovery 23
- `ClassifyUc005FeedbackTests` — discovery 15
- `ClassifyUc006AgentContractTests` — discovery 24
- `ClassifyGraphEvidenceGuardTests` — discovery 13
- `ClassifyModuleGuardTests` — discovery 11

## Content fingerprints (immutable inputs live at report write; raw SHA-256)

This report path (`docs/verification/classify-v1.md`) is **excluded** from the raw hash table: it is the
artifact being written, so a pre-write hash cannot match final bytes. Raw self-hashing
is impossible; no fabricated post-write self-hash is recorded. All hashes below are
immutable inputs only (no private/financial payloads).

| Artifact | SHA-256 | Bytes |
|---|---|---:|
| `scripts/verify-classify-module.sh` | `51ed91e0f9ff9f6f2a142ea621fc1c4109a8c3c290547a88a8f29b4989b780ca` | 35540 |
| `scripts/verify-classify-graph.sh` | `de53f6a420b85bf17fa20696ec90dc674d6f63ca106f2600cb9e556fdd5808b3` | 32668 |
| `scripts/verify-classify-contract.sh` | `79b356278574316c67d2977f6218bf576850f0ee7742ef8d16ef27e8d689234e` | 4410 |
| `scripts/verify-classify-security.sh` | `e2ca022dd0f6d81e3f9c1c70af62a7cba549b704b8cc1cb7506451a91daffd39` | 12444 |
| `scripts/verify-classify-ergonomics-security.sh` | `17a3e9b08b6880e271cb527f45e07b920400bf6d12357182ef75d63d80b4c153` | 5365 |
| `scripts/verify-classify-ergonomics-process.sh` | `55b0440a612deabc80cfe6c4933359640fcdf77c811ddcb082876345672cefc3` | 4871 |
| `tests/Tally.Tests/Classify/ClassifyModuleGuardTests.cs` | `34f1cabe0de07c05b9072cc3d1a67d7d9ea917a64603db4ba721c190bc148e39` | 15780 |
| `tests/Tally.Tests/Classify/ClassifyGraphEvidenceGuardTests.cs` | `c1783c84791b486615b0c061f5d6b6de53b6f70dd3a4bfa6449b9dfc5b420b97` | 23881 |
| `docs/verification/classify-graph.md` | `229134b8dcecea18ecf5688d7679ce1365d9a071de369f98928c29e2d8444b92` | 5809 |
| `.lexicon/graph/CLASSIFY/module.json` | `9f10ec81f359bc1d2d164c4fd58588a1ab80ab9ef26ab8a117bdd86ff558a478` | 7918 |
| `.lexicon/graph/CLASSIFY/external-dependency/EXT-CLASSIFY-LEDGER-PUBLIC-CONTRACT.json` | `cca542e8ade73916aba166e1bb4e12d0aee040e2ab6fa2a539173fa40acfaaa1` | 2505 |
| `.lexicon/graph/CLASSIFY/external-dependency/EXT-CLASSIFY-AI-AGENT-HOST.json` | `e6b24f6d393aa97d4a2d5d016c3f661085e211225017e181bf7cc362234916b9` | 1071 |
| `.lexicon/graph/CLASSIFY/external-dependency/EXT-CLASSIFY-HOST-OS-SECURITY.json` | `374020520bc1c5d4f067cdd086477e1a372e64a403fc865042db428a462f33e8` | 937 |
| `.lexicon/graph/CLASSIFY/external-dependency/EXT-CLASSIFY-PRIVATE-EVALUATION-CORPUS.json` | `0088fb75dff5518590e1d1d96afb85e86cab4b873a3049cf4a7ceb610641e29b` | 1055 |
| `docs/verification/classify-v1.md` | *(excluded — report write target; raw self-hash impossible)* | — |

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
