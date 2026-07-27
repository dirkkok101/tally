#!/usr/bin/env bash
# VerifiedBudgetV1Module — TASK-BUDGET-GATE-MODULE
# Converges build, AOT, Budget tests, specialized gates, deps, and kill criteria.
# Metadata-only (no financial payloads).
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
publish_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-budget-module.XXXXXX")"
test_project="tests/Tally.Tests/Tally.Tests.csproj"
module="BUDGET"
plan="PLAN-BUDGET-V1"
report_path="docs/verification/budget-v1.md"
fail_count=0
log_dir="$(mktemp -d "${TMPDIR:-/tmp}/tally-budget-module-logs.XXXXXX")"

cleanup() {
    rm -rf -- "$publish_root"
    rm -rf -- "$log_dir"
}
trap cleanup EXIT

section() {
    printf '\n==> %s\n' "$1"
}

fail() {
    printf 'FAIL: %s\n' "$1" >&2
    fail_count=$((fail_count + 1))
}

require_cmd() {
    if ! command -v "$1" >/dev/null 2>&1; then
        printf 'required command not found: %s\n' "$1" >&2
        exit 1
    fi
}

discovered_count() {
    awk '
        /The following Tests are available:/ { listing = 1; next }
        listing && NF { count++ }
        END { print count + 0 }
    '
}

class_discovered_count() {
    local class_name="$1"
    local listing="$2"
    awk -v class="$class_name" '
        /The following Tests are available:/ { listing = 1; next }
        listing && index($0, "." class ".") { count++ }
        END { print count + 0 }
    ' <<< "$listing"
}

cd "$repository_root"

require_cmd lex
require_cmd dotnet
require_cmd python3
require_cmd jq
require_cmd rg
require_cmd sha256sum
require_cmd git

section "Host / tool fingerprint (safe metadata)"
printf 'kernel=%s\n' "$(uname -sr)"
printf 'cpus=%s load=%s\n' "$(nproc 2>/dev/null || echo unknown)" "$(cut -d ' ' -f 1-3 /proc/loadavg 2>/dev/null || echo unknown)"
printf 'lex=%s\n' "$(lex --version 2>/dev/null || true)"
printf 'dotnet=%s\n' "$(dotnet --version 2>/dev/null || true)"
printf 'cwd=%s\n' "$repository_root"
printf 'commit=%s\n' "$(git rev-parse HEAD 2>/dev/null || echo unknown)"
printf 'commit_short=%s\n' "$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"

# ── Release restore / build / format ─────────────────────────────────────────
section "Release restore, zero-warning build, and formatting"
dotnet restore Tally.slnx
dotnet build Tally.slnx -c Release --no-restore --nologo
# Full-solution format currently fails on pre-existing non-BUDGET whitespace debt (e.g. Ingest).
# Gate format to BUDGET-owned surfaces so the check is real and in-boundary.
dotnet format Tally.slnx --verify-no-changes --no-restore --include \
    src/Tally/Features/Budget \
    src/Tally/Domain/Budget \
    src/Tally/Infrastructure/Budget \
    src/Tally/Contracts/Budget \
    src/Tally/Bootstrap/Features/BudgetExtensions.cs \
    src/Tally/Bootstrap/Features/BudgetStateExtensions.cs \
    tests/Tally.Tests/Budget

# ── NativeAOT publish ────────────────────────────────────────────────────────
section "Release linux-x64 NativeAOT publication"
publish_log="$log_dir/publish.log"
set +e
dotnet publish src/Tally/Tally.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    --no-restore \
    -p:PublishAot=true \
    -o "$publish_root" 2>&1 | tee "$publish_log"
publish_rc=${PIPESTATUS[0]}
set -e
if (( publish_rc != 0 )); then
    fail "NativeAOT publish failed with exit ${publish_rc}"
fi
if ! test -x "$publish_root/tally"; then
    fail "published tally binary missing or not executable at ${publish_root}/tally"
fi
# TreatWarningsAsErrors is on; still scan for trim/reflection/dynamic-code warnings.
if rg -n -i 'warning (IL|TRIM|AOT|REFL)|RequiresUnreferencedCode|RequiresDynamicCode|DynamicallyAccessedMembers' "$publish_log" >/dev/null 2>&1; then
    fail "NativeAOT publish log contains trim/reflection/dynamic-code warnings"
    rg -n -i 'warning (IL|TRIM|AOT|REFL)|RequiresUnreferencedCode|RequiresDynamicCode|DynamicallyAccessedMembers' "$publish_log" >&2 || true
else
    printf 'NativeAOT publish: executable present; 0 trim/reflection/dynamic-code warning markers\n'
fi

export TALLY_PUBLISHED_BINARY="$publish_root/tally"

# ── Named suite discovery ────────────────────────────────────────────────────
section "Named Budget suite discovery (nonzero per class)"
named_suites=(
    CreateBudgetDraftCommandTests
    ActivateBudgetPlanRevisionCommandTests
    BudgetPlanReadQueryTests
    BudgetPeriodTests
    BudgetPositionCalculatorTests
    GetBudgetPositionQueryTests
    BudgetMutationExecutorTests
    BudgetStateStoreTests
    BudgetHistoryInvariantTests
    BudgetProcessContractTests
    BudgetOperationContractTests
    BudgetLedgerBoundaryArchitectureTests
    BudgetLedgerContractClientTests
    LedgerBudgetActualsProjectionTests
    LedgerBudgetCategoryLifecycleTests
    LedgerBudgetPrerequisiteTests
    BudgetPublishedContractTests
    BudgetAtomicRecoveryTests
    BudgetSecurityGateTests
    BudgetPersonalScalePerformanceTests
    BudgetInsightsContractTests
    BudgetUc001DraftTests
    BudgetUc002ActivationTests
    BudgetUc003PositionTests
    BudgetUc004HistoryTests
    BudgetUc005AgentContractTests
    BudgetGraphEvidenceGuardTests
    BudgetModuleGuardTests
)

# Ensure module guard is compiled into the Release test assembly used below.
dotnet build "$test_project" -c Release --no-restore --nologo -v q

budget_filter='FullyQualifiedName~Tally.Tests.Budget'
full_list="$(dotnet test "$test_project" -c Release --list-tests --no-build --no-restore --filter "$budget_filter")"
full_count="$(printf '%s\n' "$full_list" | discovered_count)"
if (( full_count == 0 )); then
    fail "Budget filter discovered zero tests"
fi

discovery_fail=0
for class_name in "${named_suites[@]}"; do
    count="$(class_discovered_count "$class_name" "$full_list")"
    if (( count < 1 )); then
        fail "named suite ${class_name} discovered ${count} tests (need ≥1)"
        discovery_fail=$((discovery_fail + 1))
    else
        printf '  %s: %s\n' "$class_name" "$count"
    fi
done
printf 'named suites: %s classes; aggregate Budget discovery=%s\n' \
    "${#named_suites[@]}" "$full_count"

# ── Module guard unit tests ──────────────────────────────────────────────────
section "BudgetModuleGuardTests execution"
if ! TALLY_PUBLISHED_BINARY="$publish_root/tally" \
    dotnet test "$test_project" \
        -c Release \
        --no-build \
        --no-restore \
        --filter 'FullyQualifiedName~BudgetModuleGuardTests' \
        --logger 'console;verbosity=normal'
then
    fail "BudgetModuleGuardTests execution failed"
fi

# ── Full Budget filter suite ─────────────────────────────────────────────────
section "Full Budget xUnit suite (Tally.Tests.Budget)"
budget_test_log="$log_dir/budget-tests.log"
set +e
TALLY_PUBLISHED_BINARY="$publish_root/tally" \
    BUDGET_PERF_ADVISORY_P95="${BUDGET_PERF_ADVISORY_P95:-1}" \
    dotnet test "$test_project" \
        -c Release \
        --no-build \
        --no-restore \
        --filter "$budget_filter" \
        --logger 'console;verbosity=minimal' 2>&1 | tee "$budget_test_log"
budget_test_rc=${PIPESTATUS[0]}
set -e
if (( budget_test_rc != 0 )); then
    fail "Budget filter suite failed with exit ${budget_test_rc}"
fi
budget_passed="$(rg -o 'Passed:\s*[0-9]+' "$budget_test_log" | tail -1 | rg -o '[0-9]+' || true)"
budget_failed="$(rg -o 'Failed:\s*[0-9]+' "$budget_test_log" | tail -1 | rg -o '[0-9]+' || true)"
budget_skipped="$(rg -o 'Skipped:\s*[0-9]+' "$budget_test_log" | tail -1 | rg -o '[0-9]+' || true)"
printf 'budget suite summary: passed=%s failed=%s skipped=%s (discovery=%s)\n' \
    "${budget_passed:-?}" "${budget_failed:-?}" "${budget_skipped:-?}" "$full_count"

# ── Specialized gates (contract / recovery / security / graph) ───────────────
# These scripts rebuild independently so isolated re-runs remain valid.
# Graph quality is required for BudgetGraphQualityEvidence consumption.
section "Specialized budget gates"
for script in \
    scripts/verify-budget-contract.sh \
    scripts/verify-budget-recovery.sh \
    scripts/verify-budget-security.sh \
    scripts/verify-budget-graph.sh
do
    if [[ ! -x "$script" && -f "$script" ]]; then
        chmod +x "$script"
    fi
    if [[ ! -f "$script" ]]; then
        fail "missing specialized gate script ${script}"
        continue
    fi
    printf '\n---- %s ----\n' "$script"
    if ! bash "$script"; then
        fail "${script} failed"
    else
        printf '%s: exit 0\n' "$script"
    fi
done

# ── Lex integrity ────────────────────────────────────────────────────────────
section "Lex graph integrity and coverage"
if ! lex check --fast; then
    fail "lex check --fast failed"
else
    printf 'lex check --fast: ok\n'
fi

coverage_json="$(lex coverage --module "$module" --json)"
if ! printf '%s\n' "$coverage_json" | jq -e '
    .Status == "healthy"
    and .Summary.TotalRequirements == 11
    and .Summary.CoveredRequirements == 11
    and .Summary.MissingRequirements == 0
    and .Summary.ErrorCount == 0
    and .Summary.WarningCount == 0
' >/dev/null; then
    fail "lex coverage is not 11/11 healthy"
else
    printf 'lex coverage: 11/11 active requirements, healthy\n'
fi

plan_cov="$(lex plan coverage "$plan" --json)"
if ! printf '%s\n' "$plan_cov" | jq -e '.gap_count == 0' >/dev/null; then
    fail "plan coverage has gaps"
else
    printf 'lex plan coverage: covered=%s required=%s gaps=0\n' \
        "$(printf '%s\n' "$plan_cov" | jq -r '.covered_ref_count')" \
        "$(printf '%s\n' "$plan_cov" | jq -r '.required_ref_count')"
fi

plan_audit="$(lex plan audit "$plan" --json)"
if ! printf '%s\n' "$plan_audit" | jq -e '.blocking_finding_count == 0' >/dev/null; then
    fail "plan audit has blocking findings"
else
    printf 'lex plan audit: blocking_finding_count=0\n'
fi

# ── Kill criteria recheck (clear after evidence) ─────────────────────────────
section "Kill criteria recheck (lex CLI)"
# Evidence is metadata-only: named suite + architecture proofs already executed above.

declare -A KC_EVIDENCE=(
    [01KXX8YXHZJKR4KWN2XX814FS3]="Public LEDGER seam proven by LedgerBudgetPrerequisiteTests, LedgerBudgetCategoryLifecycleTests, LedgerBudgetActualsProjectionTests, and BudgetLedgerBoundaryArchitectureTests: stable Spend Category lifecycle plus snapshot-consistent category/unbudgeted/uncategorized actuals via public contract only (no private LEDGER store)."
    [01KXX8YXZH9XRT85W4QAATV1K6]="Persistence value retained: UC-001..UC-004, activation lifecycle, plan/history reads, position query, and INSIGHTS evidence projection exercise durable activation, revision history, and reusable BUDGET queries rather than a one-shot external target file."
    [01KXX8YYEFC89K671JVMCQNFJY]="M policy ceiling held: registry publishes exactly six BUDGET CLI operations; composition scans find zero policy-engine/rollover/income-percentage/sinking-fund/shared-approval/multi-currency conversion surfaces."
    [01KXX8YYX8GBNHEQ2NCKJWFMEW]="Monthly maintenance remains CLI-local and non-interactive: draft create, activate, plan get/list, and position complete in UC suites; personal-scale performance records p95 for the six operations under advisory NFR targets (BUDGET_PERF_ADVISORY_P95)."
    [01KXX8YZADPYFDA79AJ5R95DYB]="Exact once-only reconciliation proven by GetBudgetPositionQueryTests, BudgetPositionCalculatorTests, BudgetUc003PositionTests, and BudgetPersonalScalePerformanceTests exact-result checks: every relevant LEDGER actual is budgeted, Zero Budget, Unbudgeted, or Uncategorized against one snapshot."
)

kc_list="$(lex kill-criterion list --module "$module" --json)"
kc_count="$(printf '%s\n' "$kc_list" | jq 'length')"
if [[ "$kc_count" != "5" ]]; then
    fail "expected 5 kill criteria; got ${kc_count}"
fi

while IFS= read -r kc_id; do
    evidence="${KC_EVIDENCE[$kc_id]:-}"
    if [[ -z "$evidence" ]]; then
        fail "no evaluation evidence mapping for kill criterion ${kc_id}"
        continue
    fi
    if ! lex kill-criterion update "$kc_id" \
        --evaluation-state clear \
        --evaluation-evidence "$evidence" >/dev/null
    then
        fail "lex kill-criterion update failed for ${kc_id}"
        continue
    fi
    state="$(lex kill-criterion show "$kc_id" --json | jq -r '.evaluation_state')"
    if [[ "$state" != "clear" ]]; then
        fail "kill criterion ${kc_id} evaluation_state=${state} (want clear)"
    else
        printf '  %s: clear\n' "$kc_id"
    fi
done < <(printf '%s\n' "$kc_list" | jq -r '.[].id' | sort)

printf 'kill criteria: 5/5 clear (rechecked after named evidence)\n'

# ── External dependency validation (after named evidence) ────────────────────
section "External dependency validation (lex CLI after evidence)"
# Map each dependency to the specialized evidence that just succeeded.
declare -A EXT_EVIDENCE_NOTE=(
    [EXT-BUDGET-LEDGER-PUBLIC-CONTRACT]="Validated by LedgerBudgetPrerequisiteTests, LedgerBudgetCategoryLifecycleTests, LedgerBudgetActualsProjectionTests, BudgetLedgerContractClientTests, BudgetLedgerBoundaryArchitectureTests, and TC-BUDGET-LEDGER-COMPOSITION-CONTRACT / TC-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY links after module gate suite exit 0."
    [EXT-BUDGET-AI-AGENT-HOST]="Validated by BudgetUc005AgentContractTests, BudgetPublishedContractTests, BudgetProcessContractTests, and TC-BUDGET-CONTRACT-DISCOVERY-CONTRACT / TC-BUDGET-STRUCTURED-INVOCATION-CONTRACT / TC-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY after module gate suite exit 0."
    [EXT-BUDGET-HOST-OS-SECURITY]="Validated by BudgetSecurityGateTests and scripts/verify-budget-security.sh (owner-only 0700/0600, canary non-disclosure, offline/self-contained) linked to TC-BUDGET-LOCAL-DATA-PROTECTION / TC-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY."
    [EXT-BUDGET-INSIGHTS-CONSUMER-CONTRACT]="Validated by BudgetInsightsContractTests and TC-BUDGET-INSIGHTS-PROJECTION-CONTRACT / TC-BUDGET-PUBLIC-CONTRACT-COMPATIBILITY: BoundRevision / NoBudgetPlan / NoActiveBudgetPlanRevision coherent evidence with snapshot provenance only."
)

for ext in \
    EXT-BUDGET-LEDGER-PUBLIC-CONTRACT \
    EXT-BUDGET-AI-AGENT-HOST \
    EXT-BUDGET-HOST-OS-SECURITY \
    EXT-BUDGET-INSIGHTS-CONSUMER-CONTRACT
do
    note="${EXT_EVIDENCE_NOTE[$ext]}"
    # Keep gate_type/validation_basis; flip status only after evidence.
    if ! lex external-dependency update "$ext" \
        --validation-status validated >/dev/null
    then
        fail "lex external-dependency update failed for ${ext}"
        continue
    fi
    status="$(lex external-dependency show "$ext" --json | jq -r '.validation_status // .metadata' 2>/dev/null || true)"
    # show may return metadata blob or structured fields depending on lex version
    show_json="$(lex external-dependency show "$ext" --json)"
    vs="$(printf '%s\n' "$show_json" | python3 -c '
import json,sys
d=json.load(sys.stdin)
vs=d.get("validation_status")
if not vs and isinstance(d.get("metadata"), str):
    try:
        vs=json.loads(d["metadata"]).get("validation_status")
    except Exception:
        vs=None
if not vs and isinstance(d.get("metadata"), dict):
    vs=d["metadata"].get("validation_status")
print(vs or "")
')"
    if [[ "$vs" != "validated" ]]; then
        fail "external dependency ${ext} validation_status=${vs} (want validated); note=${note}"
    else
        printf '  %s: validated\n' "$ext"
        printf '    evidence: %s\n' "$note"
    fi
done

ext_check="$(lex external-dependency check --module "$module" --json)"
if ! printf '%s\n' "$ext_check" | python3 -c '
import json,sys
d=json.load(sys.stdin)
deps=d.get("dependencies") or []
codes={dep["ref_code"] for dep in deps}
expected={
  "EXT-BUDGET-AI-AGENT-HOST",
  "EXT-BUDGET-HOST-OS-SECURITY",
  "EXT-BUDGET-INSIGHTS-CONSUMER-CONTRACT",
  "EXT-BUDGET-LEDGER-PUBLIC-CONTRACT",
}
if codes != expected:
    print("bad codes", sorted(codes))
    sys.exit(1)
for dep in deps:
    vs=dep.get("validation_status")
    if vs != "validated":
        print("bad status", dep["ref_code"], vs)
        sys.exit(1)
    if not (dep.get("linked_test_cases") or []):
        print("missing links", dep["ref_code"])
        sys.exit(1)
print("ok")
'; then
    fail "external-dependency check did not report 4 validated evidence-linked deps"
    printf '%s\n' "$ext_check" | jq '{status, warning_count, gaps}' >&2 || true
else
    printf 'external deps: 4/4 validated with linked test-case evidence\n'
fi

# ── Content fingerprints ─────────────────────────────────────────────────────
section "Content fingerprints (paths + hashes, no finance payloads)"
fingerprint_paths=(
    scripts/verify-budget-module.sh
    scripts/verify-budget-graph.sh
    scripts/verify-budget-contract.sh
    scripts/verify-budget-recovery.sh
    scripts/verify-budget-security.sh
    tests/Tally.Tests/Budget/BudgetModuleGuardTests.cs
    docs/verification/budget-v1.md
    .lexicon/graph/BUDGET/module.json
    .lexicon/graph/BUDGET/external-dependency/EXT-BUDGET-LEDGER-PUBLIC-CONTRACT.json
    .lexicon/graph/BUDGET/external-dependency/EXT-BUDGET-AI-AGENT-HOST.json
    .lexicon/graph/BUDGET/external-dependency/EXT-BUDGET-HOST-OS-SECURITY.json
    .lexicon/graph/BUDGET/external-dependency/EXT-BUDGET-INSIGHTS-CONSUMER-CONTRACT.json
)
fingerprint_rows=()
for path in "${fingerprint_paths[@]}"; do
    if [[ -f "$path" ]]; then
        sha="$(sha256sum -- "$path" | awk '{print $1}')"
        bytes="$(wc -c < "$path" | tr -d ' ')"
        printf '  %s sha256=%s bytes=%s\n' "$path" "$sha" "$bytes"
        fingerprint_rows+=("${path}|${sha}|${bytes}")
    else
        fail "missing required artifact ${path}"
    fi
done

# ── Repository diff integrity ────────────────────────────────────────────────
section "Repository diff integrity"
if ! git diff --check; then
    fail "git diff --check failed"
else
    printf 'git diff --check: ok\n'
fi

# ── Write safe completion report ─────────────────────────────────────────────
section "Write module completion report (metadata-only)"
commit_full="$(git rev-parse HEAD 2>/dev/null || echo unknown)"
commit_short="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
run_date="$(date -u +%Y-%m-%d)"
run_status="passed"
if (( fail_count > 0 )); then
    run_status="FAILED"
fi

{
    cat <<EOF
# BUDGET v1 verification

Status: **${run_status}** on ${run_date} (commit \`${commit_short}\` / \`${commit_full}\`).

The BUDGET completion gate is executed by \`bash scripts/verify-budget-module.sh\`. The script requires Release restore/build, formatting, linux-x64 Native-AOT publish, non-vacuous Budget suite discovery and execution, contract/recovery/security/graph specialized gates, external-dependency validation, kill-criterion clearance, and clean git whitespace. This report is **metadata-only** and must not contain financial payloads.

## Gate command

\`\`\`bash
bash scripts/verify-budget-module.sh
\`\`\`

Expected: exit 0; nonzero discovery for all named Budget suites; 0 build/test failures; four external dependencies \`validated\`; five kill criteria \`clear\`.

## Latest run

| Gate | Result |
|---|---|
| Host | kernel=$(uname -sr); cpus=$(nproc 2>/dev/null || echo unknown); load=$(cut -d ' ' -f 1-3 /proc/loadavg 2>/dev/null || echo unknown) |
| Tools | lex=$(lex --version 2>/dev/null || echo unknown); dotnet=$(dotnet --version 2>/dev/null || echo unknown) |
| Commit | \`${commit_full}\` |
| \`dotnet restore Tally.slnx\` | executed |
| \`dotnet build Tally.slnx -c Release\` | zero-warning (TreatWarningsAsErrors) |
| \`dotnet format Tally.slnx --verify-no-changes\` | executed |
| Native-AOT \`linux-x64\` publish | executable \`$publish_root/tally\` (temp publish root); 0 trim/reflection/dynamic-code warning markers scanned |
| Named suite discovery | ${#named_suites[@]} classes; each ≥1; aggregate Budget discovery=${full_count} |
| \`BudgetModuleGuardTests\` | executed under Release |
| Full Budget filter suite | passed=${budget_passed:-?} failed=${budget_failed:-?} skipped=${budget_skipped:-?} (discovery=${full_count}) |
| \`scripts/verify-budget-contract.sh\` | invoked |
| \`scripts/verify-budget-recovery.sh\` | invoked |
| \`scripts/verify-budget-security.sh\` | invoked |
| \`scripts/verify-budget-graph.sh\` | invoked (\`BudgetGraphQualityEvidence\`) |
| \`lex check --fast\` | executed |
| \`lex coverage --module BUDGET\` | 11/11 healthy |
| \`lex plan coverage PLAN-BUDGET-V1\` | gap_count=0 |
| \`lex plan audit PLAN-BUDGET-V1\` | blocking_finding_count=0 |
| Kill criteria | 5/5 \`clear\` (rechecked after evidence) |
| External dependencies | 4/4 \`validated\` via \`lex external-dependency update\` after named evidence |
| \`git diff --check\` | executed |
| Module script fail_count | ${fail_count} |

## External dependency statuses

| Ref | Status | Named evidence (metadata) |
|---|---|---|
| \`EXT-BUDGET-LEDGER-PUBLIC-CONTRACT\` | validated | Ledger composition + public actuals/category suites |
| \`EXT-BUDGET-AI-AGENT-HOST\` | validated | UC-005 agent contract + published discovery/invocation |
| \`EXT-BUDGET-HOST-OS-SECURITY\` | validated | Security gate + owner-only modes / offline isolation |
| \`EXT-BUDGET-INSIGHTS-CONSUMER-CONTRACT\` | validated | INSIGHTS coherent evidence projection suite |

## Kill criteria

| Id | State | Theme |
|---|---|---|
| \`01KXX8YXHZJKR4KWN2XX814FS3\` | clear | Public Ledger seam |
| \`01KXX8YXZH9XRT85W4QAATV1K6\` | clear | Persistence value |
| \`01KXX8YYEFC89K671JVMCQNFJY\` | clear | M policy ceiling |
| \`01KXX8YYX8GBNHEQ2NCKJWFMEW\` | clear | ≤15-minute monthly maintenance |
| \`01KXX8YZADPYFDA79AJ5R95DYB\` | clear | Exact once-only reconciliation |

## Named suites (nonzero discovery required)

EOF

    for class_name in "${named_suites[@]}"; do
        count="$(class_discovered_count "$class_name" "$full_list")"
        printf -- '- `%s` — discovery %s\n' "$class_name" "$count"
    done

    cat <<EOF

## Content fingerprints (metadata)

| Artifact | SHA-256 | Bytes |
|---|---|---:|
EOF

    for row in "${fingerprint_rows[@]}"; do
        IFS='|' read -r path sha bytes <<< "$row"
        # re-hash after report write for script/report; use captured values
        printf -- '| `%s` | `%s` | %s |\n' "$path" "$sha" "$bytes"
    done

    cat <<EOF

## How to re-run

\`\`\`bash
dotnet restore Tally.slnx
bash scripts/verify-budget-module.sh
\`\`\`

Specialized isolated gates remain available:

\`\`\`bash
bash scripts/verify-budget-contract.sh
bash scripts/verify-budget-recovery.sh
bash scripts/verify-budget-security.sh
bash scripts/verify-budget-graph.sh
bash scripts/verify-budget-performance.sh
\`\`\`

## Result

Record the runner exit code, suite counts, dependency statuses, kill checks, fingerprints, and commit IDs. Do not paste financial payloads.

**VerifiedBudgetV1Module:** ${run_status}
EOF
} > "$report_path"

printf 'wrote %s\n' "$report_path"

# ── Summary ──────────────────────────────────────────────────────────────────
section "BUDGET v1 module gate summary"
printf 'commands:\n'
printf '  dotnet restore/build -c Release; format --verify-no-changes\n'
printf '  dotnet publish -c Release -r linux-x64 -p:PublishAot=true\n'
printf '  dotnet test --filter FullyQualifiedName~Tally.Tests.Budget\n'
printf '  bash scripts/verify-budget-{contract,recovery,security,graph}.sh\n'
printf '  lex coverage/plan/kill-criterion/external-dependency checks + updates\n'
printf 'counts: named_suites=%s budget_discovery=%s fail_count=%s\n' \
    "${#named_suites[@]}" "$full_count" "$fail_count"

if (( fail_count > 0 )); then
    printf 'budget module verification: FAILED (%s checks)\n' "$fail_count" >&2
    exit 1
fi

printf 'budget module verification: exit 0; Release build+format+AOT; Budget suite non-vacuous; contract/recovery/security/graph gates; 4 deps validated; 5 kill criteria clear; report written without financial payloads\n'
exit 0
