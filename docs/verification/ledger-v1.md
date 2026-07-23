# Ledger v1 verification

Status: passed on 2026-07-23.

The Ledger completion gate is executed by `bash scripts/verify-ledger-module.sh`. The script requires correctness, exact public-contract inventory, recovery, security, provider neutrality, all 18 use-case workflows, graph integrity, and clean formatting. The personal-scale benchmark still executes 30 measured 100,000-transaction queries and reports p50 and p95, but the initial CLI field-validation release has no blocking latency threshold.

## Evidence

`bash scripts/verify-ledger-module.sh` exited 0 with the following results:

| Command or gate | Result |
|---|---|
| `dotnet restore Tally.slnx` | Passed; all projects were up to date. |
| `dotnet build Tally.slnx --no-restore` | Passed with 0 warnings and 0 errors. |
| `dotnet format Tally.slnx --verify-no-changes --no-restore` | Passed; no formatting changes required. |
| `dotnet publish src/Tally/Tally.csproj -c Release -r linux-x64 --self-contained true --no-restore -p:PublishAot=true` | Passed; produced an executable NativeAOT `tally` binary. |
| Full test discovery | Found 1,715 tests across all 82 `*Tests.cs` classes; no planned test class had zero discovery. |
| Full xUnit suite | 1,715 passed, 0 failed, 0 skipped in 6m39s. |
| `PublicContractInventoryTests` | Discovered and passed 84/84 cases, including exactly 74 unique operation IDs and CLI paths. |
| `bash scripts/verify-ledger-core.sh` | Discovered and passed 27/27 core/storage/published-binary cases. |
| `bash scripts/verify-ledger-security.sh` | Clean build plus 339/339 security, privacy, recovery, evidence, and published-contract cases. Deployment encryption evidence was not asserted; the gate correctly refused to treat the ext4 filesystem type alone as evidence. |
| Release test build | Passed with 0 warnings and 0 errors. |
| All `Tally.Tests.EndToEnd.UC*` workflows | Found exactly 18 workflow classes and passed 322/322 cases in 4m29s. |
| `ActualsPersonalScaleTests` | Passed exact totals, grouping, pagination, and all 30 measured 100,000-transaction queries. On a 16-CPU host with load 11.87/18.41/26.33 and 18 concurrent dotnet processes, p50 was 1475.9ms and p95 was 1594.8ms; the threshold is advisory. |
| `lex check --fast` | Passed with 0 errors, 0 warnings, and 0 suppressions. |
| `lex review lint --module LEDGER --json` | Passed with 0 findings. |
| `lex coverage --module LEDGER --json` | Healthy: 25/25 active requirements covered, 0 gaps, 0 errors, 0 warnings. |
| `lex plan coverage PLAN-LEDGER-V1 --json` | 293/293 required references covered with 0 gaps. The existing 43 implementation/gate tasks without direct Implements links remain a non-blocking warning. |
| `lex plan audit PLAN-LEDGER-V1 --self-review --json` | 0 blocking findings. The existing 33 optional-generic-reference informational findings remain non-blocking. |
| `br dep cycles --json` | 0 dependency cycles. |
| `git diff --check` | Passed. |

The formal `lex review run prepare` chassis could not start because `DD-LEDGER-APPLICATION-ARCHITECTURE` has no audit-history baseline. No review pass is claimed from that chassis. The post-correction fresh-context bead review was clean, and the machine lint, coverage, plan, test, and runtime gates above all passed.

## Test coverage

The verification script is exercised end to end by the command above. This evidence report contains no application behavior and has no direct automated test. The performance-contract change is covered by `ActualsPersonalScaleTests`, including exact totals, grouping, ordering, pagination, and advisory p50/p95 reporting.
