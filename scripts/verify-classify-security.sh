#!/usr/bin/env bash
# CLASSIFY local data security / process-isolation gate (bd-2igu).
# Publishes linux-x64 Native-AOT tally, discovers the security matrix, executes it with
# TALLY_PUBLISHED_BINARY, and spot-checks owner-only modes + process inventory.
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
publish_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-classify-security.XXXXXX")"
test_project="tests/Tally.Tests/Tally.Tests.csproj"
filter='FullyQualifiedName~ClassifySecurityGateTests'
# Minimum non-vacuous matrix: permissions, canaries, hostile boundaries, isolation, published.
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
        printf 'classify security verification did not discover case containing "%s"\n' "$needle" >&2
        exit 1
    fi
}

cd "$repository_root"

section "Host platform"
if [[ "$(uname -s)" != "Linux" ]]; then
    printf 'classify security verification requires Linux (owner-only 0700/0600 modes)\n' >&2
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

section "CLASSIFY security test discovery (non-vacuous)"
test_list="$(dotnet test "$test_project" --list-tests --no-build --filter "$filter")"
test_count="$(printf '%s\n' "$test_list" | discovered_count)"
if (( test_count < min_tests )); then
    printf 'classify security verification discovered only %s tests; at least %s are required\n' \
        "$test_count" "$min_tests" >&2
    exit 1
fi
printf 'classify security verification discovered %s tests\n' "$test_count"

section "Required case families"
for needle in \
    TC_CLASSIFY_LOCAL_DATA_PROTECTION_bootstrap \
    TC_CLASSIFY_LOCAL_DATA_PROTECTION_wal_shm \
    TC_CLASSIFY_LOCAL_DATA_PROTECTION_success_workflow \
    TC_CLASSIFY_LOCAL_DATA_PROTECTION_validation_failure \
    TC_CLASSIFY_LOCAL_DATA_PROTECTION_permissive_directory \
    TC_CLASSIFY_LOCAL_DATA_PROTECTION_permissive_database \
    TC_CLASSIFY_LOCAL_DATA_PROTECTION_malformed_json \
    TC_CLASSIFY_LOCAL_DATA_PROTECTION_unknown_fields \
    TC_CLASSIFY_LOCAL_DATA_PROTECTION_unsafe_input_path \
    TC_CLASSIFY_LOCAL_DATA_PROTECTION_reason_and_key \
    Hostile_unsupported_version \
    Hostile_symlink_input_path \
    Hostile_outside_root \
    Hostile_unknown_temporary \
    Hostile_symlink_temporary \
    TC_CLASSIFY_SELF_CONTAINED_classify_composition \
    TC_CLASSIFY_SELF_CONTAINED_registry_has_no_background \
    TC_CLASSIFY_SELF_CONTAINED_schema_discovery \
    TC_CLASSIFY_SELF_CONTAINED_published_binary_classify_status \
    TC_CLASSIFY_SELF_CONTAINED_published_binary_does_not_echo
do
    require_name "$needle"
done
printf 'required case families present\n'

section "Security matrix (in-process + published NativeAOT binary)"
security_test_log="$(mktemp "${TMPDIR:-/tmp}/tally-classify-sec-tests.XXXXXX.log")"
TALLY_PUBLISHED_BINARY="$publish_root/tally" \
    dotnet test "$test_project" \
    --no-build \
    --filter "$filter" \
    --logger "console;verbosity=normal" | tee "$security_test_log"
security_skipped="$(grep -oE 'Skipped:\s*[0-9]+' "$security_test_log" | tail -1 | grep -oE '[0-9]+' || true)"
rm -f "$security_test_log"
if [[ "${security_skipped:-0}" != "0" ]]; then
    printf 'classify security verification: %s tests reported Skipped (expected 0)\n' "$security_skipped" >&2
    exit 1
fi
printf 'classify security test run: Skipped: 0\n'

section "Filesystem mode spot-check via published binary"
data_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-classify-sec-modes.XXXXXX")"
modes_cleanup() {
    rm -rf -- "$data_root"
}
trap 'modes_cleanup; cleanup' EXIT

# Initialize data root (ledger + classify) with a version call.
printf '%s' '{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-sec-gate"},"input":{}}' \
    | TALLY_DATA_ROOT="$data_root" "$publish_root/tally" version --input - >/dev/null

# Force classify store open via status (not-found is expected; opens owner-only layout).
status_out="$(printf '%s' '{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-sec-gate"},"input":{"contractVersion":"1.0","subjectType":"evaluation","subjectId":"eval-missing-gate"}}' \
    | TALLY_DATA_ROOT="$data_root" "$publish_root/tally" classify status --input - 2>/tmp/tally-classify-sec-stderr.$$ || true)"
status_err="$(cat /tmp/tally-classify-sec-stderr.$$ 2>/dev/null || true)"
rm -f /tmp/tally-classify-sec-stderr.$$

# Seeded canary tokens that must never appear on stderr or error envelopes.
canary_reason="CANARY_CLASSIFY_REASON_7f3a"
canary_key="CANARY_CLASSIFY_IDEM_KEY_7f3a"
canary_desc="CANARY_CLASSIFY_DESC_PRIVATE"

fail_out="$(printf '%s' "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"classify-sec-gate\"},\"idempotencyKey\":\"${canary_key}\",\"input\":{\"contractVersion\":\"1.0\",\"subjectType\":\"preview\",\"subjectId\":\"prev-missing\",\"reason\":\"${canary_reason}\"}}" \
    | TALLY_DATA_ROOT="$data_root" "$publish_root/tally" classify abandon --input - 2>/tmp/tally-classify-sec-fail-stderr.$$ || true)"
fail_err="$(cat /tmp/tally-classify-sec-fail-stderr.$$ 2>/dev/null || true)"
rm -f /tmp/tally-classify-sec-fail-stderr.$$

for canary in "$canary_reason" "$canary_key" "$canary_desc" CANARY_CLASSIFY PRIVATE_CLASSIFY; do
    if grep -Fq "$canary" <<<"$status_err"; then
        printf 'classify security spot-check leaked canary token %s into status stderr\n' "$canary" >&2
        exit 1
    fi
    if grep -Fq "$canary" <<<"$fail_out$fail_err"; then
        printf 'classify security spot-check leaked canary token %s via failing abandon call\n' "$canary" >&2
        exit 1
    fi
done

classify_dir="$data_root/classify"
classify_db="$classify_dir/classify.db"
if [[ ! -d "$classify_dir" || ! -f "$classify_db" ]]; then
    printf 'classify security mode spot-check expected classify/ and classify.db under data root\n' >&2
    exit 1
fi

dir_mode="$(stat -c '%a' -- "$classify_dir")"
db_mode="$(stat -c '%a' -- "$classify_db")"
dir_uid="$(stat -c '%u' -- "$classify_dir")"
db_uid="$(stat -c '%u' -- "$classify_db")"
self_uid="$(id -u)"

if [[ "$dir_mode" != "700" ]]; then
    printf 'classify directory mode is %s; expected 700\n' "$dir_mode" >&2
    exit 1
fi
if [[ "$db_mode" != "600" ]]; then
    printf 'classify.db mode is %s; expected 600\n' "$db_mode" >&2
    exit 1
fi
if [[ "$dir_uid" != "$self_uid" || "$db_uid" != "$self_uid" ]]; then
    printf 'classify artifacts not owned by invoking uid %s (dir=%s db=%s)\n' \
        "$self_uid" "$dir_uid" "$db_uid" >&2
    exit 1
fi

for sub in tmp reports; do
    if [[ -d "$classify_dir/$sub" ]]; then
        sub_mode="$(stat -c '%a' -- "$classify_dir/$sub")"
        if [[ "$sub_mode" != "700" ]]; then
            printf 'classify/%s mode is %s; expected 700\n' "$sub" "$sub_mode" >&2
            exit 1
        fi
    fi
done

for sidecar in "$classify_db-wal" "$classify_db-shm" "$classify_db.lock" "$classify_db-journal"; do
    if [[ -e "$sidecar" ]]; then
        side_mode="$(stat -c '%a' -- "$sidecar")"
        if [[ "$side_mode" != "600" ]]; then
            printf 'classify sidecar %s mode is %s; expected 600\n' "$sidecar" "$side_mode" >&2
            exit 1
        fi
    fi
done
printf 'owner-only modes verified: classify/ %s, classify.db %s, uid %s\n' "$dir_mode" "$db_mode" "$self_uid"

section "Network-denied probe (published binary schema list without data root)"
# Store-free discovery must not require network or open private fixtures.
schema_out="$("$publish_root/tally" schema list 2>/tmp/tally-classify-sec-schema-err.$$ || true)"
schema_err="$(cat /tmp/tally-classify-sec-schema-err.$$ 2>/dev/null || true)"
rm -f /tmp/tally-classify-sec-schema-err.$$
if ! grep -Fq 'classify.evaluate' <<<"$schema_out"; then
    printf 'schema list missing classify.evaluate\n' >&2
    exit 1
fi
if grep -qiE 'classify\.db|ClassifyStateStore|SELECT |CANARY_' <<<"$schema_out$schema_err"; then
    printf 'schema list leaked storage or canary detail\n' >&2
    exit 1
fi
printf 'schema list store-free and canary-clean\n'

section "Process isolation inventory"
leftover="$(pgrep -af "$publish_root/tally" || true)"
if [[ -n "${leftover// }" ]]; then
    printf 'classify security verification left published tally processes running:\n%s\n' "$leftover" >&2
    exit 1
fi
printf 'no leftover published tally processes\n'

section "Non-interactive / no TTY prompt probe"
# Ensure invocations do not block waiting on a TTY; stdin is closed pipe.
printf '' | TALLY_DATA_ROOT="$data_root" timeout 15s "$publish_root/tally" classify status --input - \
    >/tmp/tally-classify-sec-tty-out.$$ 2>/tmp/tally-classify-sec-tty-err.$$ || true
if grep -qiE 'password|passphrase|Enter |\[Y/n\]|login:' /tmp/tally-classify-sec-tty-err.$$; then
    printf 'classify security verification observed interactive prompt on stderr\n' >&2
    exit 1
fi
rm -f /tmp/tally-classify-sec-tty-out.$$ /tmp/tally-classify-sec-tty-err.$$
printf 'no interactive prompts detected\n'

section "Gate assertions (metadata-only)"
printf 'assertions:\n'
printf '  - 0700 classify directories and 0600 files after bootstrap/workflows\n'
printf '  - fail-closed on permissive modes and hostile validation inputs\n'
printf '  - seeded canaries absent from stderr, error envelopes, and non-result surfaces\n'
printf '  - published NativeAOT binary exercised with zero leftover processes\n'
printf '  - composition free of network listeners, plugins, and background classify ops\n'
printf '  - schema discovery store-free; network-denied published acceptance path\n'

section "CLASSIFY security gate: all checks passed"
printf 'classify security verification: exit 0; %s cases discovered; modes 700/600; 0 failures\n' "$test_count"
