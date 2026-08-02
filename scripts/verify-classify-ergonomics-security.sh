#!/usr/bin/env bash
# CLASSIFY operator-ergonomics privacy / recovery / no-mutation gate (bd-3mdk).
# Discovers and executes ClassifyOperatorErgonomicsSecurityTests on Linux with
# disposable synthetic roots only. Aggregate-only output — no financial payloads.
# Non-vacuous: fails when a required named family is absent or any test fails.
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
test_project="tests/Tally.Tests/Tally.Tests.csproj"
filter='FullyQualifiedName~ClassifyOperatorErgonomicsSecurityTests'
min_tests=20

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
        printf 'classify ergonomics security verification did not discover case containing "%s"\n' "$needle" >&2
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
    # Gate must never target live data; tests use Path.GetTempPath() only.
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

section "Required case families"
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
printf 'classify ergonomics security verification: exit 0; %s cases discovered across required families; 0 failures\n' \
    "$test_count"
printf 'families: privacy logging persistence filesystem crash cursor stale no-mutation composition isolation\n'
printf 'payload policy: aggregate counts and case names only; no descriptions, amounts, paths, or keys\n'
