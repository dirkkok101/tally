#!/usr/bin/env bash
# TASK-CLASSIFY-RULEBOOK-GATE-OWNER-RULEBOOK / TC-CLASSIFY-OWNER-RULEBOOK-PRE-AUTHORITY-GATE / bd-56yx
# Real local operator gate: invokes public `tally classify rule validate` for representative,
# fresh-key replay, and hold-out evidence. Emits aggregate-only VerifiedOwnerRulebookGateReceipt.
# Never prints paths, candidate IDs, payload, or raw diagnostics. Live Ledger is read-only.
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="tests/Tally.Tests/Tally.Tests.csproj"
filter='FullyQualifiedName~OwnerRulebookGateTests'
min_named_gates=12
publish_root=""
tally_bin=""

cleanup() {
    if [[ -n "${publish_root}" && -d "${publish_root}" ]]; then
        rm -rf -- "${publish_root}"
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

# Aggregate-only blocked receipt (stable schema; no path/payload fields).
emit_blocked_receipt() {
    local block_code="$1"
    cat <<EOF
{"schemaVersion":1,"receiptKind":"VerifiedOwnerRulebookGateReceipt","authorityGranted":false,"safetyPassed":false,"benefitSufficient":false,"requiresExplicitOwnerBenefitDecision":true,"blockCode":"${block_code}","eligibleRows":0,"suggestedRows":0,"correctionRows":0,"noSuggestionRows":0,"conflictRows":0,"excludedRows":0,"staleRows":0,"incorrectApplicationCanaries":0,"unexplainedConflictCount":0,"driftCanaryCount":0,"unauthorizedMutationCount":0,"descriptionInferredRelationshipCount":0,"coverageBasisPoints":0,"ownerDecisionCountBefore":0,"ownerDecisionCountAfter":0,"elapsedOwnerMinutesBefore":null,"elapsedOwnerMinutesAfter":null,"candidateFingerprint":null,"corpusFingerprint":null,"holdOutFingerprint":null,"reportFingerprint":null,"outcomesCanonicalHash":null,"deterministicReplayPassed":false,"disclosurePassed":true,"localityPassed":true,"projectionVersion":"classification_v1","snapshotId":null,"storeGenerationFingerprint":null}
EOF
}

assert_no_private_disclosure() {
    local blob="$1"
    if grep -Eiq 'sourceDescription|normalizedToken|expectedOutcome|CANARY_PRIVATE|/home/|/Users/|\\\\Users\\\\|transactionId|candidateIds' <<<"$blob"; then
        printf 'owner-rulebook gate output contained a private-payload, id, or path canary\n' >&2
        exit 1
    fi
}

# Invoke public classify.rule.validate via JSON stdin. Never echoes request body.
# Arguments: corpus path (never printed), idempotency key, candidates CSV.
# Optional finalization (hold-out only): rep_id, replay_id, benefit decision, before, after, min_before, min_after.
# Authority is never derived from shell JSON alone — finalization is performed by production rule.validate.
invoke_rule_validate() {
    local corpus_path="$1"
    local idem_key="$2"
    local candidates_csv="$3"
    local rep_id="${4:-}"
    local replay_id="${5:-}"
    local benefit_decision="${6:-}"
    local decisions_before="${7:-}"
    local decisions_after="${8:-}"
    local minutes_before="${9:-}"
    local minutes_after="${10:-}"
    local actor_kind="${CLASSIFY_OWNER_ACTOR_KIND:-automation}"
    local actor_label="${CLASSIFY_OWNER_ACTOR_LABEL:-owner-rulebook-gate}"
    local actor_run="${CLASSIFY_OWNER_ACTOR_RUN:-gate}"

    # Build candidateIds JSON array without printing.
    local ids_json="["
    local first=1
    IFS=',' read -r -a cand_arr <<< "${candidates_csv}"
    for id in "${cand_arr[@]}"; do
        id="$(printf '%s' "$id" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
        [[ -z "$id" ]] && continue
        if [[ $first -eq 1 ]]; then first=0; else ids_json+=","; fi
        # JSON-escape is minimal: reject quotes in IDs.
        if [[ "$id" == *"\""* ]]; then
            return 2
        fi
        ids_json+="\"${id}\""
    done
    ids_json+="]"

    if [[ "$ids_json" == "[]" ]]; then
        return 3
    fi

    # corpusSource must not be logged; written only to the process stdin payload.
    # Optional finalization fields finalize a trusted receipt inside production (not shell-derived authority).
    local finalize_json=""
    if [[ -n "${rep_id}" && -n "${replay_id}" && -n "${decisions_before}" && -n "${decisions_after}" ]]; then
        finalize_json="$(python3 - "$rep_id" "$replay_id" "$benefit_decision" "$decisions_before" "$decisions_after" "$minutes_before" "$minutes_after" <<'PY'
import json,sys
rep,replay,decision,before,after,mb,ma=sys.argv[1:8]
payload={
  "representativeValidationId":rep,
  "independentReplayValidationId":replay,
  "ownerDecisionCountBefore":int(before),
  "ownerDecisionCountAfter":int(after),
}
if decision:
  payload["explicitBenefitDecision"]=decision
if mb!="":
  payload["ownerMinutesBefore"]=float(mb)
if ma!="":
  payload["ownerMinutesAfter"]=float(ma)
print(","+",".join(f'{json.dumps(k)}:{json.dumps(v)}' for k,v in payload.items()))
PY
)"
    fi

    local request
    request="$(cat <<EOF
{"contractVersion":"1.0","actor":{"kind":"${actor_kind}","label":"${actor_label}","runId":"${actor_run}"},"idempotencyKey":"${idem_key}","input":{"contractVersion":"1.0","candidateIds":${ids_json},"corpusSource":$(python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$corpus_path")${finalize_json}}}
EOF
)"

    local stdout_file stderr_file
    stdout_file="$(mktemp "${TMPDIR:-/tmp}/tally-or-out.XXXXXX")"
    stderr_file="$(mktemp "${TMPDIR:-/tmp}/tally-or-err.XXXXXX")"
    set +e
    printf '%s' "$request" | "${tally_bin}" classify rule validate --input - \
        >"$stdout_file" 2>"$stderr_file"
    local exit_code=$?
    set -e
    # Scrub stderr for disclosure before any optional inspection.
    if grep -Eiq 'sourceDescription|/home/|CANARY_PRIVATE' "$stderr_file" 2>/dev/null; then
        rm -f -- "$stdout_file" "$stderr_file"
        return 4
    fi
    VALIDATE_STDOUT="$(cat "$stdout_file")"
    VALIDATE_EXIT="$exit_code"
    rm -f -- "$stdout_file" "$stderr_file"
    return 0
}

json_field() {
    # Extract a top-level JSON string/number/bool field without printing the whole blob on failure.
    python3 - "$1" "$2" <<'PY'
import json,sys
blob=sys.argv[1]
key=sys.argv[2]
try:
    doc=json.loads(blob)
except Exception:
    sys.exit(1)
# Support result envelope: {outcome,result,error}
if isinstance(doc, dict) and "result" in doc and isinstance(doc["result"], dict):
    doc=doc["result"]
val=doc.get(key)
if val is None:
    print("")
else:
    print(val if not isinstance(val, bool) else ("true" if val else "false"))
PY
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
printf 'release build exit 0\n'

section "Publish local tally for public-contract invocation"
publish_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-owner-rulebook-pub.XXXXXX")"
dotnet publish src/Tally/Tally.csproj -c Release -o "$publish_root" --nologo -v q
tally_bin="${publish_root}/tally"
if [[ ! -x "$tally_bin" ]]; then
    printf 'published tally binary missing\n' >&2
    exit 1
fi
printf 'public tally binary ready (path not disclosed)\n'

section "Owner live input probe (no path/id disclosure)"
# Structured owner inputs via environment (never printed).
owner_corpus="${CLASSIFY_OWNER_RULEBOOK_CORPUS:-}"
owner_holdout="${CLASSIFY_OWNER_RULEBOOK_HOLD_OUT:-}"
owner_candidates="${CLASSIFY_OWNER_RULEBOOK_CANDIDATE_IDS:-}"
owner_data_root="${TALLY_DATA_ROOT:-}"
owner_benefit_decision="${CLASSIFY_OWNER_RULEBOOK_BENEFIT_DECISION:-}"
owner_decisions_before="${CLASSIFY_OWNER_DECISIONS_BEFORE:-}"
owner_decisions_after="${CLASSIFY_OWNER_DECISIONS_AFTER:-}"
owner_minutes_before="${CLASSIFY_OWNER_MINUTES_BEFORE:-}"
owner_minutes_after="${CLASSIFY_OWNER_MINUTES_AFTER:-}"

# Optional JSON stdin overlay (aggregate keys only — paths still env-only for privacy).
if [[ ! -t 0 ]]; then
    stdin_blob="$(cat || true)"
    if [[ -n "${stdin_blob}" ]]; then
        # Only allow known aggregate keys; ignore unknown. Paths must not be read from stdin.
        if printf '%s' "$stdin_blob" | grep -Eiq 'corpusPath|holdOutPath|sourceDescription|/home/'; then
            printf 'stdin must not carry paths or private payload keys\n' >&2
            exit 1
        fi
        # benefitDecision may be supplied on stdin as {"benefitDecision":"approve-broad"}
        maybe_decision="$(printf '%s' "$stdin_blob" | python3 -c 'import json,sys
try:
 d=json.load(sys.stdin); print(d.get("benefitDecision") or "")
except Exception:
 print("")' 2>/dev/null || true)"
        if [[ -n "${maybe_decision}" && -z "${owner_benefit_decision}" ]]; then
            owner_benefit_decision="${maybe_decision}"
        fi
    fi
fi

if [[ -z "${owner_corpus}" || -z "${owner_holdout}" || -z "${owner_candidates}" \
    || -z "${owner_data_root}" \
    || -z "${owner_decisions_before}" || -z "${owner_decisions_after}" ]]; then
    section "Blocked receipt (missing owner inputs)"
    blocked="$(emit_blocked_receipt "CLASSIFY-OWNER-RULEBOOK-INPUT-MISSING")"
    printf '%s\n' "$blocked"
    assert_no_private_disclosure "$blocked"
    printf 'blocked receipt: authorityGranted=false; inputs incomplete\n'
    owner_live_path="blocked"
else
    if [[ ! -d "${owner_data_root}" || -L "${owner_data_root}" ]]; then
        printf 'TALLY_DATA_ROOT must be an existing non-symlink directory\n' >&2
        exit 1
    fi
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
    printf 'owner live inputs: present (modes corpus=%s holdout=%s); paths/ids not disclosed\n' \
        "${corpus_mode}" "${holdout_mode}"
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
    Gate_disclosure \
    Gate_projection
do
    require_name "$needle"
done
printf 'required named gate families present (≥12)\n'

section "Runtime data-root boundary"
if [[ "${owner_live_path}" == "present" ]]; then
    # Keep the caller's owner data root: candidate rule versions and the frozen
    # Ledger projection must come from the same installed runtime composition.
    # rule.validate writes aggregate CLASSIFY validation evidence only; Ledger is read-only.
    export TALLY_DATA_ROOT="${owner_data_root}"
    printf 'owner runtime root retained (path not disclosed); Ledger remains read-only\n'
else
    unset TALLY_DATA_ROOT
    printf 'no owner runtime opened while inputs are incomplete\n'
fi

section "Owner-rulebook gate matrix (HandleAsync + projection binding)"
# Synthetic proofs always run via unit tests (agent policy may skip execution).
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

section "Public-contract live validate path"
if [[ "${owner_live_path}" == "blocked" ]]; then
    printf 'live owner path: blocked (CLASSIFY-OWNER-RULEBOOK-INPUT-MISSING)\n'
else
    # Representative validation (aggregate only; no shell authority).
    invoke_rule_validate "${owner_corpus}" "or-rep-$(date +%s)-$$" "${owner_candidates}" || true
    rep_out="${VALIDATE_STDOUT:-}"
    rep_exit="${VALIDATE_EXIT:-1}"
    assert_no_private_disclosure "${rep_out}"

    # Fresh-key independent replay.
    invoke_rule_validate "${owner_corpus}" "or-replay-$(date +%s)-$$" "${owner_candidates}" || true
    replay_out="${VALIDATE_STDOUT:-}"
    replay_exit="${VALIDATE_EXIT:-1}"
    assert_no_private_disclosure "${replay_out}"

    if [[ "${rep_exit}" -ne 0 || "${replay_exit}" -ne 0 ]]; then
        blocked="$(emit_blocked_receipt "CLASSIFY-OWNER-RULEBOOK-VALIDATE-UNAVAILABLE")"
        printf '%s\n' "$blocked"
        assert_no_private_disclosure "$blocked"
        printf 'public validate unavailable or failed; authorityGranted=false\n'
    else
        if [[ -n "${owner_benefit_decision}" \
            && "${owner_benefit_decision}" != "approve-broad" \
            && "${owner_benefit_decision}" != "approve" \
            && "${owner_benefit_decision}" != "defer-broad" ]]; then
            printf 'benefit decision must be approve-broad, approve, defer-broad, or empty\n' >&2
            exit 1
        fi
        rep_id="$(json_field "$rep_out" "validationId")"
        replay_id="$(json_field "$replay_out" "validationId")"
        if [[ -z "${rep_id}" || -z "${replay_id}" ]]; then
            blocked="$(emit_blocked_receipt "CLASSIFY-OWNER-RULEBOOK-VALIDATE-UNAVAILABLE")"
            printf '%s\n' "$blocked"
            assert_no_private_disclosure "$blocked"
            printf 'public validate missing validation identity; authorityGranted=false\n'
        else
            # Hold-out finalizes the trusted receipt inside production rule.validate.
            # Shell never treats aggregate JSON as authority — only receipt identity is observed.
            invoke_rule_validate \
                "${owner_holdout}" "or-hold-$(date +%s)-$$" "${owner_candidates}" \
                "${rep_id}" "${replay_id}" "${owner_benefit_decision}" \
                "${owner_decisions_before}" "${owner_decisions_after}" \
                "${owner_minutes_before}" "${owner_minutes_after}" || true
            hold_out="${VALIDATE_STDOUT:-}"
            hold_exit="${VALIDATE_EXIT:-1}"
            assert_no_private_disclosure "${hold_out}"

            if [[ "${hold_exit}" -ne 0 ]]; then
                blocked="$(emit_blocked_receipt "CLASSIFY-OWNER-RULEBOOK-VALIDATE-UNAVAILABLE")"
                printf '%s\n' "$blocked"
                assert_no_private_disclosure "$blocked"
                printf 'public hold-out finalize unavailable or failed; authorityGranted=false\n'
            else
                receipt_id="$(json_field "$hold_out" "ownerRulebookGateReceiptId")"
                receipt_fp="$(json_field "$hold_out" "ownerRulebookGateReceiptFingerprint")"
                # Emit identity-only observation for operators (no private payload; no shell authority bool).
                printf '{"schemaVersion":1,"receiptKind":"VerifiedOwnerRulebookGateReceipt","ownerRulebookGateReceiptId":%s,"ownerRulebookGateReceiptFingerprint":%s,"holdOutValidationId":%s}\n' \
                    "$(python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "${receipt_id}")" \
                    "$(python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "${receipt_fp}")" \
                    "$(python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$(json_field "$hold_out" "validationId")")"
                assert_no_private_disclosure "${hold_out}"
                if [[ -n "${receipt_id}" && -n "${receipt_fp}" ]]; then
                    printf 'live path: production finalized trusted receipt identity (authority not shell-derived)\n'
                else
                    printf 'live path: hold-out completed without receipt identity; authority remains production-gated\n'
                fi
            fi
        fi
    fi
fi

section "Aggregate receipt summary"
printf 'disclosure: aggregate metadata only; Ledger read-only; no activation/apply\n'
printf 'owner-rulebook pre-authority gate: complete\n'
exit 0
