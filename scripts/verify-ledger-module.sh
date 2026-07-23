#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
publish_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-ledger-module.XXXXXX")"
test_project="tests/Tally.Tests/Tally.Tests.csproj"

cleanup() {
    rm -rf -- "$publish_root"
}
trap cleanup EXIT

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

cd "$repository_root"

section "Restore, zero-warning build, and formatting"
dotnet restore Tally.slnx
dotnet build Tally.slnx --no-restore
dotnet format Tally.slnx --verify-no-changes --no-restore

section "Release linux-x64 NativeAOT publication"
dotnet publish src/Tally/Tally.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    --no-restore \
    -p:PublishAot=true \
    -o "$publish_root"
test -x "$publish_root/tally"

section "Non-vacuous full-suite discovery"
full_test_list="$(dotnet test "$test_project" --list-tests --no-build --no-restore)"
full_test_count="$(printf '%s\n' "$full_test_list" | discovered_count)"
if (( full_test_count == 0 )); then
    printf 'Ledger module verification discovered zero tests\n' >&2
    exit 1
fi

test_class_count=0
while IFS= read -r test_file; do
    class_name="$(basename "$test_file" .cs)"
    if ! grep -Fq ".${class_name}." <<< "$full_test_list"; then
        printf 'Ledger module verification did not discover tests for %s\n' "$test_file" >&2
        exit 1
    fi
    test_class_count=$((test_class_count + 1))
done < <(rg --files tests/Tally.Tests -g '*Tests.cs' | sort)
printf 'Ledger module verification discovered %s tests across %s test classes\n' "$full_test_count" "$test_class_count"

section "Full xUnit suite"
TALLY_PUBLISHED_BINARY="$publish_root/tally" \
    dotnet test "$test_project" \
    --no-build \
    --no-restore \
    --logger "console;verbosity=minimal"

section "Exact 74-operation public contract"
public_contract_filter='FullyQualifiedName~PublicContractInventoryTests'
public_contract_list="$(dotnet test "$test_project" \
    --list-tests \
    --no-build \
    --no-restore \
    --filter "$public_contract_filter")"
public_contract_count="$(printf '%s\n' "$public_contract_list" | discovered_count)"
if (( public_contract_count != 84 )); then
    printf 'Public contract verification discovered %s tests; exactly 84 are required\n' "$public_contract_count" >&2
    exit 1
fi
printf 'Public contract verification discovered %s tests\n' "$public_contract_count"
dotnet test "$test_project" \
    --no-build \
    --no-restore \
    --filter "$public_contract_filter" \
    --logger "console;verbosity=minimal"

section "Core and provider-neutral security gates"
bash scripts/verify-ledger-core.sh
bash scripts/verify-ledger-security.sh

section "All 18 published use-case workflows"
dotnet build "$test_project" -c Release --no-restore
use_case_filter='FullyQualifiedName~Tally.Tests.EndToEnd.UC'
use_case_list="$(dotnet test "$test_project" \
    -c Release \
    --list-tests \
    --no-build \
    --no-restore \
    --filter "$use_case_filter")"
use_case_count="$(printf '%s\n' "$use_case_list" | discovered_count)"
use_case_class_count=0
while IFS= read -r test_file; do
    class_name="$(basename "$test_file" .cs)"
    if ! grep -Fq ".${class_name}." <<< "$use_case_list"; then
        printf 'Use-case verification did not discover %s\n' "$class_name" >&2
        exit 1
    fi
    use_case_class_count=$((use_case_class_count + 1))
done < <(rg --files tests/Tally.Tests/EndToEnd -g 'UC*Tests.cs' | sort)
if (( use_case_class_count != 18 )); then
    printf 'Use-case verification found %s classes; exactly 18 are required\n' "$use_case_class_count" >&2
    exit 1
fi
printf 'Use-case verification discovered %s tests across all %s workflows\n' "$use_case_count" "$use_case_class_count"
TALLY_PUBLISHED_BINARY="$publish_root/tally" \
    dotnet test "$test_project" \
    -c Release \
    --no-build \
    --no-restore \
    --filter "$use_case_filter" \
    --logger "console;verbosity=minimal"

section "Advisory personal-scale timing with blocking correctness"
printf 'performance environment: kernel=%s cpus=%s load=%s concurrent-dotnet-processes=%s\n' \
    "$(uname -sr)" \
    "$(nproc)" \
    "$(cut -d ' ' -f 1-3 /proc/loadavg)" \
    "$(pgrep -c dotnet || true)"
dotnet test "$test_project" \
    -c Release \
    --no-build \
    --no-restore \
    --filter "FullyQualifiedName~ActualsPersonalScaleTests" \
    --logger "console;verbosity=normal"

section "Lex graph and tracker integrity"
lex check --fast

lint_json="$(lex review lint --module LEDGER --json)"
printf '%s\n' "$lint_json" | jq -e '.passed == true and .finding_count == 0' >/dev/null
printf 'Lex documentation lint: 0 findings\n'

coverage_json="$(lex coverage --module LEDGER --json)"
printf '%s\n' "$coverage_json" | jq -e '
    .Status == "healthy"
    and .Summary.TotalRequirements == 25
    and .Summary.CoveredRequirements == 25
    and .Summary.MissingRequirements == 0
    and .Summary.ErrorCount == 0
    and .Summary.WarningCount == 0
' >/dev/null
printf 'Lex coverage: 25/25 active requirements, 0 gaps\n'

plan_coverage_json="$(lex plan coverage PLAN-LEDGER-V1 --json)"
printf '%s\n' "$plan_coverage_json" | jq -e '.gap_count == 0' >/dev/null
printf 'Lex plan coverage: 0 blocking gaps\n'

plan_audit_json="$(lex plan audit PLAN-LEDGER-V1 --self-review --json)"
printf '%s\n' "$plan_audit_json" | jq -e '.blocking_finding_count == 0' >/dev/null
printf 'Lex plan audit: 0 blocking findings\n'

cycle_json="$(br dep cycles --json)"
printf '%s\n' "$cycle_json" | jq -e '.count == 0' >/dev/null
printf 'Tracker dependency cycles: 0\n'

section "Repository diff integrity"
git diff --check

printf '\nLedger v1 module verification passed.\n'
