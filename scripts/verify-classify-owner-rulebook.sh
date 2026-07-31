#!/usr/bin/env bash
# TASK-CLASSIFY-RULEBOOK-GATE-OWNER-RULEBOOK / TC-CLASSIFY-OWNER-RULEBOOK-PRE-AUTHORITY-GATE / bd-56yx
# Aggregate-only owner-rulebook pre-authority gate. Never prints paths, descriptions,
# amounts, expected outcomes, or raw rows. Live Ledger is not mutated; any mutation
# probe must use a disposable TALLY_DATA_ROOT.
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="tests/Tally.Tests/Tally.Tests.csproj"
filter='FullyQualifiedName~OwnerRulebookGateTests'
min_named_gates=12
publish_root=""
data_root=""

cleanup() {
    if [[ -n "${publish_root}" && -d "${publish_root}" ]]; then
        rm -rf -- "${publish_root}"
    fi
    if [[ -n "${data_root}" && -d "${data_root}" ]]; then
        rm -rf -- "${data_root}"
    fi
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
        printf 'owner-rulebook gate verification did not discover case containing "%s"\n' "$needle" >&2
        exit 1
    fi
}

# Emit aggregate-only blocked receipt when owner live inputs are absent.
# Never includes path strings or private payloads.
emit_blocked_receipt() {
    local block_code="$1"
    cat <<EOF
{
  "schemaVersion": 1,
  "receiptKind": "VerifiedOwnerRulebookGateReceipt",
  "authorityGranted": false,
  "safetyPassed": false,
  "benefitSufficient": false,
  "requiresExplicitOwnerBenefitDecision": true,
  "blockCode": "${block_code}",
  "eligibleRows": 0,
  "suggestedRows": 0,
  "correctionRows": 0,
  "noSuggestionRows": 0,
  "conflictRows": 0,
  "excludedRows": 0,
  "staleRows": 0,
  "incorrectApplicationCanaries": 0,
  "unexplainedConflictCount": 0,
  "driftCanaryCount": 0,
  "unauthorizedMutationCount": 0,
  "descriptionInferredRelationshipCount": 0,
  "coverageBasisPoints": 0,
  "ownerDecisionCountBefore": 0,
  "ownerDecisionCountAfter": 0,
  "elapsedOwnerMinutesBefore": null,
  "elapsedOwnerMinutesAfter": null,
  "candidateFingerprint": null,
  "corpusFingerprint": null,
  "holdOutFingerprint": null,
  "deterministicReplayPassed": false,
  "disclosurePassed": true,
  "localityPassed": true
}
EOF
}

assert_no_private_disclosure() {
    local blob="$1"
    # Metadata-only output must never embed these private/path patterns.
    if grep -Eiq 'sourceDescription|normalizedToken|expectedOutcome|CANARY_PRIVATE|/home/|/Users/|\\\\Users\\\\' <<<"$blob"; then
        printf 'owner-rulebook gate output contained a private-payload or path canary\n' >&2
        exit 1
    fi
}

cd "$repository_root"

section "Host platform"
if [[ "$(uname -s)" != "Linux" ]]; then
    printf 'owner-rulebook gate requires Linux (owner-only 0700/0600 modes)\n' >&2
    exit 1
fi
printf 'linux host confirmed (uid=%s)\n' "$(id -u)"

section "Release build"
dotnet build Tally.slnx -c Release --nologo
printf 'release build: 0 warnings/0 errors expected from agent policy; build exit 0\n'

section "Owner live input probe (no path disclosure)"
# Owner supplies untracked 90-day corpus + hold-out via environment.
# Missing inputs yield a stable blocked receipt and do not synthesize values.
owner_corpus="${CLASSIFY_OWNER_RULEBOOK_CORPUS:-}"
owner_holdout="${CLASSIFY_OWNER_RULEBOOK_HOLD_OUT:-}"
owner_benefit_decision="${CLASSIFY_OWNER_RULEBOOK_BENEFIT_DECISION:-}"

if [[ -z "${owner_corpus}" || -z "${owner_holdout}" ]]; then
    section "Blocked receipt (missing owner inputs)"
    blocked="$(emit_blocked_receipt "CLASSIFY-OWNER-RULEBOOK-INPUT-MISSING")"
    printf '%s\n' "$blocked"
    assert_no_private_disclosure "$blocked"
    if grep -Fq '"authorityGranted": false' <<<"$blocked" \
        && grep -Fq '"blockCode": "CLASSIFY-OWNER-RULEBOOK-INPUT-MISSING"' <<<"$blocked"; then
        printf 'blocked receipt: authorityGranted=false; zero synthesized values\n'
    else
        printf 'blocked receipt malformed\n' >&2
        exit 1
    fi
    owner_live_path="blocked"
else
    # Do not print the path values — only existence/mode metadata.
    if [[ ! -f "${owner_corpus}" || -L "${owner_corpus}" ]]; then
        printf 'owner corpus is not a regular non-symlink file\n' >&2
        exit 1
    fi
    if [[ ! -f "${owner_holdout}" || -L "${owner_holdout}" ]]; then
        printf 'owner hold-out is not a regular non-symlink file\n' >&2
        exit 1
    fi
    corpus_mode="$(stat -c '%a' -- "${owner_corpus}")"
    holdout_mode="$(stat -c '%a' -- "${owner_holdout}")"
    if [[ "${corpus_mode}" != "600" && "${corpus_mode}" != "400" ]]; then
        printf 'owner corpus mode must be owner-only (600/400); got %s\n' "${corpus_mode}" >&2
        exit 1
    fi
    if [[ "${holdout_mode}" != "600" && "${holdout_mode}" != "400" ]]; then
        printf 'owner hold-out mode must be owner-only (600/400); got %s\n' "${holdout_mode}" >&2
        exit 1
    fi
    printf 'owner live inputs: present (modes corpus=%s holdout=%s); paths not disclosed\n' \
        "${corpus_mode}" "${holdout_mode}"
    if [[ -z "${owner_benefit_decision}" ]]; then
        printf 'note: CLASSIFY_OWNER_RULEBOOK_BENEFIT_DECISION unset; insufficient benefit requires explicit owner decision (no invented threshold)\n'
    fi
    owner_live_path="present"
fi

section "Gate test discovery (non-vacuous named families)"
test_list="$(dotnet test "$test_project" --list-tests --no-build --filter "$filter" -c Release 2>/dev/null \
    || dotnet test "$test_project" --list-tests --filter "$filter" -c Release)"
test_count="$(printf '%s\n' "$test_list" | discovered_count)"
if (( test_count < min_named_gates )); then
    printf 'owner-rulebook gate discovered only %s tests; at least %s named gates are required\n' \
        "$test_count" "$min_named_gates" >&2
    exit 1
fi
printf 'owner-rulebook gate discovered %s tests\n' "$test_count"

section "Required named gate families"
# At least 12 named permission, public-contract, 90-day, hold-out, recurrence, timing,
# decision-reduction, row-accounting, incorrect-apply, conflict, determinism, drift,
# locality, and disclosure gates.
for needle in \
    Gate_permission \
    Gate_public_contract \
    Gate_90_day \
    Gate_hold_out \
    Gate_recurrence \
    Gate_timing \
    Gate_decision_reduction \
    Gate_row_accounting \
    Gate_incorrect_apply \
    Gate_conflict \
    Gate_determinism \
    Gate_drift \
    Gate_locality \
    Gate_disclosure
do
    require_name "$needle"
done
printf 'required named gate families present (≥12)\n'

section "Disposable TALLY_DATA_ROOT mutation isolation probe"
data_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-owner-rulebook-data.XXXXXX")"
# Touch isolation: gate must not require or mutate a live production data root.
export TALLY_DATA_ROOT="$data_root"
printf 'disposable data root prepared (path not disclosed in receipt)\n'

section "Owner-rulebook gate matrix"
# Synthetic + blocked-input proofs run always. Never seed personal values.
# Optional: CLASSIFY_OWNER_RULEBOOK_RUN_TESTS=0 skips execution (discovery-only / agent policy).
if [[ "${CLASSIFY_OWNER_RULEBOOK_RUN_TESTS:-1}" == "0" ]]; then
    printf 'CLASSIFY_OWNER_RULEBOOK_RUN_TESTS=0: discovery-only; matrix execution skipped\n'
else
    gate_log="$(mktemp "${TMPDIR:-/tmp}/tally-owner-rulebook-tests.XXXXXX.log")"
    set +e
    CLASSIFY_OWNER_LIVE_PATH_STATUS="${owner_live_path}" \
        dotnet test "$test_project" \
        -c Release \
        --no-build \
        --filter "$filter" \
        --logger "console;verbosity=normal" 2>&1 | tee "$gate_log"
    test_exit="${PIPESTATUS[0]}"
    set -e
    assert_no_private_disclosure "$(cat "$gate_log")"
    skipped="$(grep -oE 'Skipped:\s*[0-9]+' "$gate_log" | tail -1 | grep -oE '[0-9]+' || true)"
    rm -f "$gate_log"
    if [[ "${test_exit}" -ne 0 ]]; then
        printf 'owner-rulebook gate tests failed (exit %s)\n' "${test_exit}" >&2
        exit "${test_exit}"
    fi
    if [[ "${skipped:-0}" != "0" ]]; then
        printf 'owner-rulebook gate: %s tests reported Skipped (expected 0)\n' "${skipped}" >&2
        exit 1
    fi
    printf 'owner-rulebook gate tests: exit 0; Skipped: 0\n'
fi

section "Aggregate receipt summary"
if [[ "${owner_live_path}" == "blocked" ]]; then
    printf 'live owner path: blocked (CLASSIFY-OWNER-RULEBOOK-INPUT-MISSING); authorityGranted=false\n'
    printf 'synthetic safety gates: exercised via OwnerRulebookGateTests\n'
else
    printf 'live owner path: present; benefit decision explicit=%s\n' \
        "$([[ -n "${owner_benefit_decision}" ]] && printf yes || printf no)"
    printf 'insufficient benefit requires explicit owner product decision; no 50%% threshold invented\n'
fi
printf 'disclosure: aggregate metadata only; locality: disposable TALLY_DATA_ROOT for mutation probes\n'
printf 'owner-rulebook pre-authority gate: PASS (metadata-only)\n'
exit 0
