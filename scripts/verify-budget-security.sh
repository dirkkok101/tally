#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
publish_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-budget-security.XXXXXX")"
test_project="tests/Tally.Tests/Tally.Tests.csproj"
filter='FullyQualifiedName~BudgetSecurityGateTests'
# Minimum non-vacuous matrix: permissions, canaries, hostile boundaries, isolation.
min_tests=20

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

require_name() {
    local needle="$1"
    if ! grep -Fq "$needle" <<< "$test_list"; then
        printf 'budget security verification did not discover case containing "%s"\n' "$needle" >&2
        exit 1
    fi
}

cd "$repository_root"

section "Host platform"
if [[ "$(uname -s)" != "Linux" ]]; then
    printf 'budget security verification requires Linux (owner-only 0700/0600 modes)\n' >&2
    exit 1
fi
printf 'linux host confirmed (uid=%s)\n' "$(id -u)"

section "Build"
dotnet build Tally.slnx --nologo

section "Release linux-x64 NativeAOT publication"
dotnet publish src/Tally/Tally.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishAot=true \
    -o "$publish_root"
test -x "$publish_root/tally"
printf 'published binary: %s\n' "$publish_root/tally"

section "Budget security test discovery (non-vacuous)"
test_list="$(dotnet test "$test_project" --list-tests --no-build --filter "$filter")"
test_count="$(printf '%s\n' "$test_list" | discovered_count)"
if (( test_count < min_tests )); then
    printf 'budget security verification discovered only %s tests; at least %s are required\n' \
        "$test_count" "$min_tests" >&2
    exit 1
fi
printf 'budget security verification discovered %s tests\n' "$test_count"

section "Required case families"
for needle in \
    TC_BUDGET_LOCAL_DATA_PROTECTION_bootstrap \
    TC_BUDGET_LOCAL_DATA_PROTECTION_wal_shm \
    TC_BUDGET_LOCAL_DATA_PROTECTION_success_workflow \
    TC_BUDGET_LOCAL_DATA_PROTECTION_validation_failure \
    TC_BUDGET_LOCAL_DATA_PROTECTION_permissive_directory \
    TC_BUDGET_LOCAL_DATA_PROTECTION_permissive_database \
    TC_BUDGET_LOCAL_DATA_PROTECTION_malformed_json \
    TC_BUDGET_LOCAL_DATA_PROTECTION_unknown_fields \
    TC_BUDGET_LOCAL_DATA_PROTECTION_unsafe_input_path \
    TC_BUDGET_LOCAL_DATA_PROTECTION_success_amount \
    TC_BUDGET_LOCAL_DATA_PROTECTION_idempotency_conflict \
    Hostile_unsupported_version \
    Hostile_over_limit \
    Hostile_symlink \
    TC_BUDGET_SELF_CONTAINED_budget_composition \
    TC_BUDGET_SELF_CONTAINED_published_binary
do
    require_name "$needle"
done
printf 'required case families present\n'

section "Security matrix (in-process + published NativeAOT binary)"
TALLY_PUBLISHED_BINARY="$publish_root/tally" \
    dotnet test "$test_project" \
    --no-build \
    --filter "$filter" \
    --logger "console;verbosity=normal"

section "Filesystem mode spot-check via published binary"
data_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-budget-sec-modes.XXXXXX")"
modes_cleanup() {
    rm -rf -- "$data_root"
}
trap 'modes_cleanup; cleanup' EXIT

# Initialize data root (ledger + budget) with a version call.
printf '%s' '{"contractVersion":"1.0","actor":{"kind":"automation","label":"budget-sec-gate"},"input":{}}' \
    | TALLY_DATA_ROOT="$data_root" "$publish_root/tally" version --input - >/dev/null

# Force budget store open via list (empty lifecycle is fine).
list_out="$(printf '%s' '{"contractVersion":"1.0","actor":{"kind":"automation","label":"budget-sec-gate"},"input":{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"}}}' \
    | TALLY_DATA_ROOT="$data_root" "$publish_root/tally" budget plan revision list --input - 2>/tmp/tally-budget-sec-stderr.$$ || true)"
list_err="$(cat /tmp/tally-budget-sec-stderr.$$ 2>/dev/null || true)"
rm -f /tmp/tally-budget-sec-stderr.$$

# Canary absence on this probe surface.
for canary in CANARY_BUDGET 999888777 PRIVATE_BUDGET; do
    if grep -Fq "$canary" <<<"$list_out$list_err"; then
        printf 'budget security mode spot-check leaked canary token %s\n' "$canary" >&2
        exit 1
    fi
done

budget_dir="$data_root/budget"
budget_db="$budget_dir/budget.db"
if [[ ! -d "$budget_dir" || ! -f "$budget_db" ]]; then
    printf 'budget security mode spot-check expected budget/ and budget.db under data root\n' >&2
    exit 1
fi

dir_mode="$(stat -c '%a' -- "$budget_dir")"
db_mode="$(stat -c '%a' -- "$budget_db")"
dir_uid="$(stat -c '%u' -- "$budget_dir")"
db_uid="$(stat -c '%u' -- "$budget_db")"
self_uid="$(id -u)"

if [[ "$dir_mode" != "700" ]]; then
    printf 'budget directory mode is %s; expected 700\n' "$dir_mode" >&2
    exit 1
fi
if [[ "$db_mode" != "600" ]]; then
    printf 'budget.db mode is %s; expected 600\n' "$db_mode" >&2
    exit 1
fi
if [[ "$dir_uid" != "$self_uid" || "$db_uid" != "$self_uid" ]]; then
    printf 'budget artifacts not owned by invoking uid %s (dir=%s db=%s)\n' \
        "$self_uid" "$dir_uid" "$db_uid" >&2
    exit 1
fi

for sidecar in "$budget_db-wal" "$budget_db-shm" "$budget_db.lock" "$budget_db.atomic"; do
    if [[ -e "$sidecar" ]]; then
        side_mode="$(stat -c '%a' -- "$sidecar")"
        if [[ "$side_mode" != "600" ]]; then
            printf 'budget sidecar %s mode is %s; expected 600\n' "$sidecar" "$side_mode" >&2
            exit 1
        fi
    fi
done
printf 'owner-only modes verified: budget/ %s, budget.db %s, uid %s\n' "$dir_mode" "$db_mode" "$self_uid"

section "Process isolation inventory"
# No leftover tally children from this gate's published invocations.
leftover="$(pgrep -af "$publish_root/tally" || true)"
if [[ -n "${leftover// }" ]]; then
    printf 'budget security verification left published tally processes running:\n%s\n' "$leftover" >&2
    exit 1
fi
printf 'no leftover published tally processes\n'

section "Gate assertions (metadata-only)"
printf 'assertions:\n'
printf '  - 0700 budget directories and 0600 files after bootstrap/workflows\n'
printf '  - fail-closed on permissive modes and hostile validation inputs\n'
printf '  - seeded canaries absent from stderr, error envelopes, and non-result surfaces\n'
printf '  - structured stdout success is the only financial payload channel\n'
printf '  - published NativeAOT binary exercised with zero leftover processes\n'
printf '  - composition free of network listeners, plugins, and background budget ops\n'

section "Budget security gate: all checks passed"
printf 'budget security verification: exit 0; %s cases discovered; modes 700/600; 0 failures\n' "$test_count"
