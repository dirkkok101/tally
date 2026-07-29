#!/usr/bin/env bash
# BUDGET inner-loop verification — wall-clock target under 60s on a warm host.
#
# Runs pure envelope + calculator + contract-shape suites only.
# Does NOT run: Native AOT publish, personal-scale performance, full UC matrices,
# security/recovery/contract ship gates, or the complete Budget xUnit surface.
#
# Ship / module completion gate (slow, intentional):
#   bash scripts/verify-budget-module.sh
#
# Extended pure+query/provenance surface (still no AOT/perf):
#   BUDGET_FAST_EXTENDED=1 bash scripts/verify-budget-fast.sh
#
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="tests/Tally.Tests/Tally.Tests.csproj"
# Soft target 60s; hard fail leaves headroom for cold JIT / loaded host.
max_seconds_soft="${BUDGET_FAST_SOFT_SECONDS:-60}"
max_seconds_hard="${BUDGET_FAST_HARD_SECONDS:-90}"
extended="${BUDGET_FAST_EXTENDED:-0}"

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
start_epoch="$(date +%s)"

section "Host fingerprint (safe metadata)"
printf 'kernel=%s\n' "$(uname -sr)"
printf 'dotnet=%s\n' "$(dotnet --version 2>/dev/null || true)"
printf 'cwd=%s\n' "$repository_root"
printf 'commit=%s\n' "$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
printf 'extended=%s soft=%ss hard=%ss\n' "$extended" "$max_seconds_soft" "$max_seconds_hard"

section "Build (Debug, quiet)"
dotnet build Tally.slnx --nologo -v q

# Core pure suites — correctness of envelope math and additive contract surface.
filter='FullyQualifiedName~BudgetContractShapeTests|FullyQualifiedName~BudgetPositionCalculatorTests|FullyQualifiedName~BudgetEnvelopeResolutionTests|FullyQualifiedName~BudgetEnvelopeIntegrityTests'
declare -A suite_floor=(
    [BudgetContractShapeTests]=4
    [BudgetPositionCalculatorTests]=39
    [BudgetEnvelopeResolutionTests]=10
    [BudgetEnvelopeIntegrityTests]=5
)

if [[ "$extended" == "1" ]]; then
    filter+='|FullyQualifiedName~GetBudgetPositionQueryTests|FullyQualifiedName~BudgetEnvelopeProvenanceTests'
    suite_floor[GetBudgetPositionQueryTests]=40
    suite_floor[BudgetEnvelopeProvenanceTests]=3
fi

section "Fast suite discovery (non-vacuous per class)"
test_list="$(dotnet test "$test_project" --list-tests --no-build --no-restore --filter "$filter")"
total="$(printf '%s\n' "$test_list" | discovered_count)"
if (( total == 0 )); then
    printf 'budget fast verification discovered zero tests\n' >&2
    exit 1
fi

discovery_fail=0
for class_name in "${!suite_floor[@]}"; do
    count="$(class_discovered_count "$class_name" "$test_list")"
    floor="${suite_floor[$class_name]}"
    if (( count < floor )); then
        printf 'FAIL: %s discovered %s tests (need ≥%s)\n' "$class_name" "$count" "$floor" >&2
        discovery_fail=$((discovery_fail + 1))
    else
        printf '  %s: %s (floor %s)\n' "$class_name" "$count" "$floor"
    fi
done
if (( discovery_fail > 0 )); then
    exit 1
fi
printf 'discovery: %s tests across %s named suites\n' "$total" "${#suite_floor[@]}"

section "Run fast suites"
dotnet test "$test_project" \
    --no-build \
    --no-restore \
    --nologo \
    --filter "$filter" \
    --logger 'console;verbosity=minimal'

elapsed=$(( $(date +%s) - start_epoch ))
printf '\n==> Wall clock\n'
printf 'elapsed=%ss soft_target=%ss hard_limit=%ss\n' \
    "$elapsed" "$max_seconds_soft" "$max_seconds_hard"

if (( elapsed > max_seconds_hard )); then
    printf 'FAIL: budget fast verification exceeded hard limit (%ss > %ss). Investigate cold builds or suite bloat.\n' \
        "$elapsed" "$max_seconds_hard" >&2
    exit 1
fi
if (( elapsed > max_seconds_soft )); then
    printf 'WARN: exceeded soft target (%ss > %ss); still under hard limit.\n' \
        "$elapsed" "$max_seconds_soft" >&2
fi

section "Budget fast verification summary"
printf 'mode=%s discovered=%s elapsed=%ss\n' \
    "$([[ "$extended" == "1" ]] && echo extended || echo core)" \
    "$total" \
    "$elapsed"
printf 'excluded: NativeAOT, personal-scale perf, full UC matrices, ship gates\n'
printf 'ship gate: bash scripts/verify-budget-module.sh\n'
printf 'budget fast verification: exit 0\n'
exit 0
