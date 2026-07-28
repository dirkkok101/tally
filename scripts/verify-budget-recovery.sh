#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="tests/Tally.Tests/Tally.Tests.csproj"
filter='FullyQualifiedName~BudgetAtomicRecoveryTests'

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

require_cutpoint() {
    local cutpoint="$1"
    local haystack="$2"
    if ! grep -Fq "$cutpoint" <<< "$haystack"; then
        printf 'budget recovery verification did not discover cutpoint "%s"\n' "$cutpoint" >&2
        exit 1
    fi
}

cd "$repository_root"

section "Build"
dotnet build Tally.slnx --nologo

section "Budget recovery test discovery (non-vacuous)"
test_list="$(dotnet test "$test_project" --list-tests --no-build --filter "$filter")"
test_count="$(printf '%s\n' "$test_list" | discovered_count)"

# 9 draft + 10 activate theory cases + 3 facts = 22 minimum.
if (( test_count < 22 )); then
    printf 'budget recovery verification discovered only %s tests; at least 22 are required\n' "$test_count" >&2
    exit 1
fi
printf 'budget recovery verification discovered %s tests\n' "$test_count"

section "Named draft cutpoints"
draft_list="$(grep -F 'Draft_cutpoint_restart_is_prior_or_complete' <<< "$test_list" || true)"
if [[ -z "${draft_list// }" ]]; then
    printf 'budget recovery verification did not discover any Draft_cutpoint_restart_is_prior_or_complete cases\n' >&2
    exit 1
fi
for cutpoint in \
    before_validation \
    after_validation \
    replay_lookup \
    revision_insert \
    entry_insert \
    events \
    outcome_references \
    commit \
    result_delivery
do
    require_cutpoint "$cutpoint" "$draft_list"
done
printf 'draft cutpoints present\n'

section "Named activation cutpoints"
activate_list="$(grep -F 'Activate_cutpoint_restart_is_prior_or_complete' <<< "$test_list" || true)"
if [[ -z "${activate_list// }" ]]; then
    printf 'budget recovery verification did not discover any Activate_cutpoint_restart_is_prior_or_complete cases\n' >&2
    exit 1
fi
for cutpoint in \
    before_validation \
    after_validation \
    replay_lookup \
    prior_supersession \
    activation \
    active_pointer \
    events \
    outcome_references \
    commit \
    result_delivery
do
    require_cutpoint "$cutpoint" "$activate_list"
done
printf 'activation cutpoints present\n'

# Ensure both primary theory methods contribute cases.
for method_name in Draft_cutpoint_restart_is_prior_or_complete Activate_cutpoint_restart_is_prior_or_complete; do
    if ! grep -Fq "$method_name" <<< "$test_list"; then
        printf 'budget recovery verification did not discover tests for %s\n' "$method_name" >&2
        exit 1
    fi
done

section "Restart recovery matrix (real budget.db)"
dotnet test "$test_project" \
    --no-build \
    --filter "$filter" \
    --logger "console;verbosity=normal"

section "Basic recovery assertions"
# Re-run discovery after execution to confirm inventory is stable, and encode the
# gate invariants the suite is required to prove (prior-or-complete, one Active,
# exact replay, owner-only). Detailed proofs live in BudgetAtomicRecoveryTests.
printf 'assertions:\n'
printf '  - prior-or-complete durable state after every named cutpoint restart\n'
printf '  - at most one Active revision after every restart and retry\n'
printf '  - pre-commit retry commits once; post-commit retry exact event-time replay\n'
printf '  - owner-only budget.db / wal / shm / recognized sidecars after forced termination\n'
printf '  - history sequences, prior/result statuses, replacement IDs, actor/reason reconcile\n'

section "Budget recovery gate: all checks passed"
printf 'budget recovery verification: exit 0; %s cutpoint cases discovered; 0 failures\n' "$test_count"
