#!/usr/bin/env bash
# CLASSIFY operator-ergonomics published-process throughput gate (bd-2byd).
# Publishes linux-x64 Native-AOT tally, discovers ClassifyOperatorErgonomicsProcessTests,
# executes them with TALLY_PUBLISHED_BINARY, and fails on zero/missing named cases or
# bound violations. Aggregate-only output (counts, wall time, RSS) — no financial payloads.
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
publish_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-erg-process.XXXXXX")"
test_project="tests/Tally.Tests/Tally.Tests.csproj"
filter='FullyQualifiedName~ClassifyOperatorErgonomicsProcessTests'
min_tests=13

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
        printf 'classify ergonomics process verification did not discover required scenario "%s"\n' "$needle" >&2
        exit 1
    fi
}

cd "$repository_root"

section "Host platform"
if [[ "$(uname -s)" != "Linux" ]]; then
    printf 'classify ergonomics process verification requires Linux (Native-AOT + owner-only roots)\n' >&2
    exit 1
fi
printf 'linux host confirmed (uid=%s euid=%s)\n' "$(id -u)" "$(id -u)"

section "Live data root guard"
if [[ -d /home/ubuntu/.local/share/tally ]]; then
    printf 'live TALLY_DATA_ROOT exists on host; gate will not open or mutate it\n'
fi
printf 'fixture roots: disposable temp only (never /home/ubuntu/.local/share/tally)\n'

section "Build"
dotnet build Tally.slnx -c Release --nologo

section "Release linux-x64 NativeAOT publication"
# Publish time is part of gate evidence (not hidden); not folded into the 5s invocation bound.
publish_start="$(date +%s.%N)"
dotnet publish src/Tally/Tally.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishAot=true \
    -o "$publish_root"
publish_end="$(date +%s.%N)"
test -x "$publish_root/tally"
printf 'published binary: %s\n' "$publish_root/tally"
printf 'publish_wall_seconds=%s\n' "$(awk -v a="$publish_start" -v b="$publish_end" 'BEGIN{printf "%.3f", b-a}')"

export TALLY_PUBLISHED_BINARY="$publish_root/tally"

section "CLASSIFY ergonomics process test discovery (non-vacuous)"
test_list="$(dotnet test "$test_project" -c Release --no-build --list-tests --filter "$filter")"
test_count="$(printf '%s\n' "$test_list" | discovered_count)"
if (( test_count < min_tests )); then
    printf 'classify ergonomics process verification discovered only %s tests; at least %s are required\n' \
        "$test_count" "$min_tests" >&2
    exit 1
fi
if (( test_count == 0 )); then
    printf 'classify ergonomics process verification discovered zero tests\n' >&2
    exit 1
fi
printf 'classify ergonomics process verification discovered %s tests\n' "$test_count"

section "Required exact scenarios"
for needle in \
    TC_ERGONOMICS_PROCESS_146_rows_page_size_500_one_invocation_within_bounds \
    TC_ERGONOMICS_PROCESS_multi_page_invocation_count_equals_ceiling \
    TC_ERGONOMICS_PROCESS_page_size_1_and_500_no_duplicates_replay_stable_fingerprint \
    TC_ERGONOMICS_PROCESS_five_additive_cli_paths_emit_one_structured_envelope \
    TC_ERGONOMICS_PROCESS_file_input_json_matches_descriptor_for_outcome_list \
    TC_ERGONOMICS_PROCESS_typed_cursor_invalid_exit_mapping_and_private_safe_stderr \
    TC_ERGONOMICS_PROCESS_typed_lifecycle_missing_eval_exit_mapping \
    TC_ERGONOMICS_PROCESS_typed_unsupported_version_exit_mapping \
    TC_ERGONOMICS_PROCESS_typed_privacy_rejected_exit_mapping \
    TC_ERGONOMICS_PROCESS_typed_resource_limit_exit_mapping \
    TC_ERGONOMICS_PROCESS_typed_integrity_exit_mapping \
    TC_ERGONOMICS_PROCESS_outcome_ids_compose_selected_outcomes_preview_without_outcome_get \
    TC_ERGONOMICS_PROCESS_zero_child_per_row_and_live_root_isolation
do
    require_name "$needle"
    printf 'scenario present: %s\n' "$needle"
done

section "Execute published-process matrix"
# No five-minute soft timeout wrapper — xUnit/host defaults apply; 5s is enforced inside the 146-row case.
dotnet test "$test_project" \
    -c Release \
    --no-build \
    --filter "$filter" \
    --logger 'console;verbosity=normal'

section "Aggregate summary"
printf 'classify ergonomics process verification: exit 0; %s cases discovered; exact scenarios present; 0 failures\n' \
    "$test_count"
printf 'evidence fields: invocations wall_ms peak_rss_bytes returned_count child_max (aggregate-only)\n'
printf 'bounds: exactly 146 returned rows @ pageSize 500 in 1 invocation; wall < 5s; peak RSS < 256 MiB; child_max = 0\n'
printf 'payload policy: no descriptions amounts paths or live-root tokens\n'
