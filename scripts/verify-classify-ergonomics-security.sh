#!/usr/bin/env bash
# CLASSIFY operator-ergonomics privacy / recovery / no-mutation gate (bd-3mdk).
# Discovers and executes ClassifyOperatorErgonomicsSecurityTests on Linux with
# disposable synthetic roots only. Aggregate-only output — no financial payloads.
# Non-vacuous: fails when a required named scenario is absent or any test fails.
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="tests/Tally.Tests/Tally.Tests.csproj"
filter='FullyQualifiedName~ClassifyOperatorErgonomicsSecurityTests'
min_tests=28

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
        printf 'classify ergonomics security verification did not discover required scenario "%s"\n' "$needle" >&2
        exit 1
    fi
}

cd "$repository_root"

section "Host platform"
if [[ "$(uname -s)" != "Linux" ]]; then
    printf 'classify ergonomics security verification requires Linux (owner-only 0700/0600 modes)\n' >&2
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

section "CLASSIFY ergonomics security test discovery (non-vacuous)"
test_list="$(dotnet test "$test_project" -c Release --no-build --list-tests --filter "$filter")"
test_count="$(printf '%s\n' "$test_list" | discovered_count)"
if (( test_count < min_tests )); then
    printf 'classify ergonomics security verification discovered only %s tests; at least %s are required\n' \
        "$test_count" "$min_tests" >&2
    exit 1
fi
if (( test_count == 0 )); then
    printf 'classify ergonomics security verification discovered zero tests\n' >&2
    exit 1
fi
printf 'classify ergonomics security verification discovered %s tests\n' "$test_count"

section "Required exact scenarios (not broad family substrings alone)"
# Exact scenario tokens Hermes required for non-vacuous proof.
for needle in \
    TC_ERGONOMICS_PRIVACY_unresolved_stdout_may_expose_owner_normalized_description \
    TC_ERGONOMICS_PRIVACY_forbidden_sinks_exclude_canaries_after_unresolved_report \
    TC_ERGONOMICS_LOGGING_cursor_bytes_exclude_descriptions_and_paths \
    TC_ERGONOMICS_PERSISTENCE_corpus_receipt_excludes_destination_and_labels \
    TC_ERGONOMICS_FILESYSTEM_symlink_destination_fails_without_write \
    TC_ERGONOMICS_FILESYSTEM_hard_linked_destination_is_not_overwritten \
    TC_ERGONOMICS_FILESYSTEM_group_writable_parent_fails_privacy \
    TC_ERGONOMICS_FILESYSTEM_wrong_owner_0600_file_fails_closed \
    TC_ERGONOMICS_FILESYSTEM_wrong_owner_0700_directory_fails_closed \
    TC_ERGONOMICS_CRASH_interrupt_before_publish_via_fault_seam_leaves_no_destination \
    TC_ERGONOMICS_CRASH_interrupt_after_publish_before_cleanup_throws_and_preserves_destination \
    TC_ERGONOMICS_CRASH_cleanup_replay_after_post_publish_interrupt_is_idempotent \
    TC_ERGONOMICS_CURSOR_malformed_continuation_fails_closed_with_null_result \
    TC_ERGONOMICS_STALE_voided_transaction_fails_unresolved_without_writes \
    TC_ERGONOMICS_NO_MUTATION_query_failure_preserves_classify_db_hash \
    TC_ERGONOMICS_NO_MUTATION_successful_queries_do_not_mutate_ledger_allocations \
    TC_ERGONOMICS_NO_MUTATION_corpus_success_only_creates_authorized_destination \
    TC_ERGONOMICS_COMPOSITION_outcome_list_ids_compose_selected_outcomes_preview \
    TC_ERGONOMICS_ENVELOPE_expected_missing_evaluation_fails_stable_exit_null_result \
    TC_ERGONOMICS_ENVELOPE_expected_unsupported_version_fails_compatibility_without_mutation \
    TC_ERGONOMICS_ENVELOPE_injected_unexpected_malformed_json_is_private_safe \
    TC_ERGONOMICS_ENVELOPE_corpus_build_missing_idempotency_fails_before_destination \
    TC_ERGONOMICS_ISOLATION_live_tally_data_root_is_never_the_fixture_root
do
    require_name "$needle"
    printf 'scenario present: %s\n' "$needle"
done

section "Required family coverage (aggregate)"
for needle in \
    TC_ERGONOMICS_PRIVACY_ \
    TC_ERGONOMICS_LOGGING_ \
    TC_ERGONOMICS_PERSISTENCE_ \
    TC_ERGONOMICS_FILESYSTEM_ \
    TC_ERGONOMICS_CRASH_ \
    TC_ERGONOMICS_CURSOR_ \
    TC_ERGONOMICS_STALE_ \
    TC_ERGONOMICS_NO_MUTATION_ \
    TC_ERGONOMICS_COMPOSITION_ \
    TC_ERGONOMICS_ENVELOPE_ \
    TC_ERGONOMICS_ISOLATION_
do
    require_name "$needle"
    printf 'family present: %s\n' "$needle"
done

section "Execute security matrix"
dotnet test "$test_project" \
    -c Release \
    --no-build \
    --filter "$filter" \
    --logger 'console;verbosity=normal'

section "Aggregate summary"
printf 'classify ergonomics security verification: exit 0; %s cases discovered; exact scenarios present; 0 failures\n' \
    "$test_count"
printf 'families: privacy logging persistence filesystem crash cursor stale no-mutation composition envelope isolation\n'
printf 'payload policy: aggregate counts and case names only; no descriptions, amounts, paths, or keys\n'
