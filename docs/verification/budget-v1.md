# BUDGET v1 verification

Status: **passed** on 2026-07-27 (commit `9c581d9` / `9c581d974a6c97c1f939456e22872e914f3e112b`).

The BUDGET completion gate is executed by `bash scripts/verify-budget-module.sh`. The script requires Release restore/build, formatting, linux-x64 Native-AOT publish, non-vacuous Budget suite discovery and execution, contract/recovery/security/graph specialized gates, external-dependency validation, kill-criterion clearance, and clean git whitespace. This report is **metadata-only** and must not contain financial payloads.

## Gate command

```bash
bash scripts/verify-budget-module.sh
```

Expected: exit 0; nonzero discovery for all named Budget suites; 0 build/test failures; four external dependencies `validated`; five kill criteria `clear`.

## Latest run

| Gate | Result |
|---|---|
| Host | kernel=Linux 7.0.0-28-generic; cpus=16; load=8.17 5.52 4.73 |
| Tools | lex=0.5.12; dotnet=10.0.110 |
| Commit | `9c581d974a6c97c1f939456e22872e914f3e112b` |
| `dotnet restore Tally.slnx` | executed |
| `dotnet build Tally.slnx -c Release` | zero-warning (TreatWarningsAsErrors) |
| `dotnet format` (BUDGET-owned paths) | verify-no-changes |
| Native-AOT `linux-x64` publish | executable `/tmp/tally-budget-module.xVX3Gp/tally` (temp publish root); 0 trim/reflection/dynamic-code warning markers scanned |
| Named suite discovery | 28 classes; each ≥1; aggregate Budget discovery=644 |
| `BudgetModuleGuardTests` | executed under Release |
| Full Budget filter suite | passed=644 failed=0 skipped=0 (discovery=644) |
| `scripts/verify-budget-contract.sh` | invoked |
| `scripts/verify-budget-recovery.sh` | invoked |
| `scripts/verify-budget-security.sh` | invoked |
| `scripts/verify-budget-graph.sh` | invoked (`BudgetGraphQualityEvidence`) |
| `lex check --fast` | executed |
| `lex coverage --module BUDGET` | 11/11 healthy |
| `lex plan coverage PLAN-BUDGET-V1` | gap_count=0 |
| `lex plan audit PLAN-BUDGET-V1` | blocking_finding_count=0 |
| Kill criteria | 5/5 `clear` (rechecked after evidence) |
| External dependencies | 4/4 `validated` via `lex external-dependency update` after named evidence |
| `git diff --check` | executed |
| Module script fail_count | 0 |

## External dependency statuses

| Ref | Status | Named evidence (metadata) |
|---|---|---|
| `EXT-BUDGET-LEDGER-PUBLIC-CONTRACT` | validated | Ledger composition + public actuals/category suites |
| `EXT-BUDGET-AI-AGENT-HOST` | validated | UC-005 agent contract + published discovery/invocation |
| `EXT-BUDGET-HOST-OS-SECURITY` | validated | Security gate + owner-only modes / offline isolation |
| `EXT-BUDGET-INSIGHTS-CONSUMER-CONTRACT` | validated | INSIGHTS coherent evidence projection suite |

## Kill criteria

| Id | State | Theme |
|---|---|---|
| `01KXX8YXHZJKR4KWN2XX814FS3` | clear | Public Ledger seam |
| `01KXX8YXZH9XRT85W4QAATV1K6` | clear | Persistence value |
| `01KXX8YYEFC89K671JVMCQNFJY` | clear | M policy ceiling |
| `01KXX8YYX8GBNHEQ2NCKJWFMEW` | clear | ≤15-minute monthly maintenance |
| `01KXX8YZADPYFDA79AJ5R95DYB` | clear | Exact once-only reconciliation |

## Named suites (nonzero discovery required)

- `CreateBudgetDraftCommandTests` — discovery 34
- `ActivateBudgetPlanRevisionCommandTests` — discovery 33
- `BudgetPlanReadQueryTests` — discovery 26
- `BudgetPeriodTests` — discovery 34
- `BudgetPositionCalculatorTests` — discovery 45
- `GetBudgetPositionQueryTests` — discovery 41
- `BudgetMutationExecutorTests` — discovery 22
- `BudgetStateStoreTests` — discovery 28
- `BudgetHistoryInvariantTests` — discovery 15
- `BudgetProcessContractTests` — discovery 31
- `BudgetOperationContractTests` — discovery 32
- `BudgetLedgerBoundaryArchitectureTests` — discovery 7
- `BudgetLedgerContractClientTests` — discovery 19
- `LedgerBudgetActualsProjectionTests` — discovery 17
- `LedgerBudgetCategoryLifecycleTests` — discovery 12
- `LedgerBudgetPrerequisiteTests` — discovery 30
- `BudgetPublishedContractTests` — discovery 15
- `BudgetAtomicRecoveryTests` — discovery 22
- `BudgetSecurityGateTests` — discovery 25
- `BudgetPersonalScalePerformanceTests` — discovery 1
- `BudgetInsightsContractTests` — discovery 25
- `BudgetUc001DraftTests` — discovery 28
- `BudgetUc002ActivationTests` — discovery 26
- `BudgetUc003PositionTests` — discovery 27
- `BudgetUc004HistoryTests` — discovery 15
- `BudgetUc005AgentContractTests` — discovery 21
- `BudgetGraphEvidenceGuardTests` — discovery 6
- `BudgetModuleGuardTests` — discovery 7

## Content fingerprints (metadata)

| Artifact | SHA-256 | Bytes |
|---|---|---:|
| `scripts/verify-budget-module.sh` | `e41dab8f400c52028383296c57b5eec0943a29ee28f46af83e1290ee13c78728` | 24945 |
| `scripts/verify-budget-graph.sh` | `1fb997df79ec5912f8655483ddaa6a5c89b6e6f2efb7654d45b81f92dfd1cad1` | 22028 |
| `scripts/verify-budget-contract.sh` | `c5b52805a0b659e392208cf461c4a8e3c2ab570d77437e76421729b70ea7f83c` | 3528 |
| `scripts/verify-budget-recovery.sh` | `e36c11df9298d672f1b3a7f1403720688767b460410c3b4c04508d757a17d234` | 3275 |
| `scripts/verify-budget-security.sh` | `14019b3cb4363172351ce03f3614e8e991c6de3b394d5d82c2e3ead95a889d56` | 6731 |
| `tests/Tally.Tests/Budget/BudgetModuleGuardTests.cs` | `ddda2435b70179b3aecf02cf350ecc03deb0a2125f5d1bd5970decd42ffeaacb` | 8491 |
| `docs/verification/budget-v1.md` | `f567c5b0a4df912f12814c0fc01b927c5a657129637e237b7b9152d2210a56cf` | 2812 |
| `.lexicon/graph/BUDGET/module.json` | `41feba97a98cd3fb2dbef04a799159ee46abfcafad710eef8c25f0de35555a9b` | 12619 |
| `.lexicon/graph/BUDGET/external-dependency/EXT-BUDGET-LEDGER-PUBLIC-CONTRACT.json` | `580d2e4423161a3ac4c065f702b3115a88e705b8824cd1c45662241700e465bd` | 689 |
| `.lexicon/graph/BUDGET/external-dependency/EXT-BUDGET-AI-AGENT-HOST.json` | `52976044b69f64a7341b1715e717e55e947c160a53842ff328290a17eb145588` | 716 |
| `.lexicon/graph/BUDGET/external-dependency/EXT-BUDGET-HOST-OS-SECURITY.json` | `273d6fb06fb4325b7e5147d36235ce323cb2022ead537d12fe6e4687226d739f` | 681 |
| `.lexicon/graph/BUDGET/external-dependency/EXT-BUDGET-INSIGHTS-CONSUMER-CONTRACT.json` | `b3613db7f4989d40fb7274ce714aa8197c1a6f22e8ec598a1449a46764d57fa3` | 996 |

## How to re-run

```bash
dotnet restore Tally.slnx
bash scripts/verify-budget-module.sh
```

Specialized isolated gates remain available:

```bash
bash scripts/verify-budget-contract.sh
bash scripts/verify-budget-recovery.sh
bash scripts/verify-budget-security.sh
bash scripts/verify-budget-graph.sh
bash scripts/verify-budget-performance.sh
```

## Result

Record the runner exit code, suite counts, dependency statuses, kill checks, fingerprints, and commit IDs. Do not paste financial payloads.

**VerifiedBudgetV1Module:** passed
