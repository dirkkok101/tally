#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="tests/Tally.Tests/Tally.Tests.csproj"
filter='FullyQualifiedName~BudgetPersonalScalePerformanceTests'
min_tests=1

section() {
    printf '\n==> %s\n' "$1"
}

discovered_count() {
    awk '
        /The following Tests are available:/ { listing = 1; next }
        listing && NF { count++ }
        END { print count + 0 }
    '
}

require_name() {
    local needle="$1"
    if ! grep -Fq "$needle" <<< "$test_list"; then
        printf 'budget performance verification did not discover case containing "%s"\n' "$needle" >&2
        exit 1
    fi
}

cd "$repository_root"

section "Host platform"
if [[ "$(uname -s)" != "Linux" ]]; then
    printf 'budget performance verification requires Linux\n' >&2
    exit 1
fi
printf 'linux host confirmed (uid=%s cpus=%s)\n' "$(id -u)" "$(nproc 2>/dev/null || echo unknown)"

section "Build"
dotnet build Tally.slnx --nologo

section "Budget performance test discovery (non-vacuous)"
test_list="$(dotnet test "$test_project" --list-tests --no-build --filter "$filter")"
test_count="$(printf '%s\n' "$test_list" | discovered_count)"
if (( test_count < min_tests )); then
    printf 'budget performance verification discovered only %s tests; at least %s are required\n' \
        "$test_count" "$min_tests" >&2
    exit 1
fi
printf 'budget performance verification discovered %s tests\n' "$test_count"

section "Required case families"
for needle in \
    TC_BUDGET_PERSONAL_SCALE_PERFORMANCE_six_operations_meet_p95_targets
do
    require_name "$needle"
done
printf 'required performance cases present\n'

section "Personal-scale benchmark (100 samples × 6 ops; network offline; no migration/backup overlap)"
# Long-running gate: seed 100k ledger txns + 1000 periods/revisions/entries, then measure.
# Timeout guidance: plan verification allows 2400s.
# p95 NFR comparison hard-fails by default (measurements always recorded regardless).
# Set BUDGET_PERF_ADVISORY_P95=1 to treat p95 budgets as advisory on a contended shared host.
export BUDGET_PERF_ADVISORY_P95="${BUDGET_PERF_ADVISORY_P95:-0}"
printf 'BUDGET_PERF_ADVISORY_P95=%s\n' "$BUDGET_PERF_ADVISORY_P95"
dotnet test "$test_project" \
    --no-build \
    --filter "$filter" \
    --logger "console;verbosity=normal"

section "Gate assertions (metadata-only)"
printf 'assertions:\n'
printf '  - load scale: 100000 active ledger transactions, 1000 periods, 1000 selected revisions, 1000 entries\n'
printf '  - >=100 measured samples per operation after warm-up\n'
printf '  - p95 NFR targets: position + insight evidence <= 6s; draft, activate, get <= 2s; list <= 1s\n'
printf '  - p95 comparison hard when BUDGET_PERF_ADVISORY_P95=0 (default); advisory when =1\n'
printf '  - exact-result reconciliation enabled on every sample\n'
printf '  - peak resident memory and mean output size reported\n'
printf '  - environment fingerprint reported; no financial fixture payloads in report\n'
printf '  - benchmark not part of normal task-level unit suites\n'

section "Budget performance gate: all checks passed"
printf 'budget performance verification: exit 0; %s cases discovered; 0 failures\n' "$test_count"
