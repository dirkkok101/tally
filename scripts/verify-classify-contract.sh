#!/usr/bin/env bash
# Published-binary CLASSIFY public contract gate (bd-3g6y).
# Discovery is non-vacuous; execution of full suites is owned by the final all-beads job.
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
publish_root="$(mktemp -d "${TMPDIR:-/tmp}/tally-classify-contract.XXXXXX")"
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

section "CLASSIFY contract test discovery (non-vacuous)"
filter='FullyQualifiedName~ClassifyPublishedContractTests|FullyQualifiedName~ClassifyProcessContractTests|FullyQualifiedName~ClassifyOperationContractTests'
test_list="$(dotnet test "$test_project" --list-tests --no-build --filter "$filter")"
test_count="$(printf '%s\n' "$test_list" | discovered_count)"
if (( test_count < 30 )); then
    printf 'classify contract verification discovered only %s tests; at least 30 are required\n' "$test_count" >&2
    exit 1
fi
printf 'classify contract verification discovered %s tests\n' "$test_count"

for class_name in ClassifyPublishedContractTests ClassifyProcessContractTests; do
    if ! grep -Fq ".${class_name}." <<< "$test_list"; then
        printf 'classify contract verification did not discover tests for %s\n' "$class_name" >&2
        exit 1
    fi
done

section "CLASSIFY contract tests"
dotnet test "$test_project" --no-build --filter "$filter"

section "Published binary: schema list includes exactly seventeen CLASSIFY ops (C12 + five additive)"
schema_list="$("$publish_root/tally" schema list)"
classify_count="$(printf '%s\n' "$schema_list" | python3 -c '
import json,sys
doc=json.load(sys.stdin)
ops=doc.get("result",{}).get("operations",doc.get("operations",[]))
ids=[str(o.get("operationId","")) for o in ops if str(o.get("operationId","")).startswith("classify.")]
print(len(ids))
c12=["classify.evaluate","classify.outcome.get","classify.apply.preview","classify.apply.run",
 "classify.rule.save","classify.rule.validate","classify.rule.activate","classify.rule.retire",
 "classify.feedback.record","classify.status","classify.abandon","classify.cleanup"]
missing=[x for x in c12 if x not in ids]
if missing:
    print("missing_c12", ",".join(missing), file=sys.stderr)
    raise SystemExit(2)
')"
if [[ "$classify_count" != "17" ]]; then
    printf 'published binary schema list has %s classify ops; expected 17 (ReleasedC12=12 + five additive)\n' "$classify_count" >&2
    exit 1
fi
printf 'published binary schema list: classify_ops=17 including ReleasedC12=12\n'

section "Published binary: evaluate schema carries limits; ledger omits limits"
eval_schema="$("$publish_root/tally" schema show classify.evaluate)"
account_schema="$("$publish_root/tally" schema show ledger.account.create)"
printf '%s\n' "$eval_schema" | python3 -c '
import json,sys
doc=json.load(sys.stdin)
result=doc.get("result") or doc
op=(result.get("operation") if isinstance(result, dict) else None) or result
assert isinstance(op, dict), "schema show missing operation object"
assert "limits" in op and op["limits"] is not None, "classify.evaluate missing limits"
assert "max_transaction_count" in op["limits"], "stable wire name missing"
'
printf '%s\n' "$account_schema" | python3 -c '
import json,sys
doc=json.load(sys.stdin)
result=doc.get("result") or doc
op=(result.get("operation") if isinstance(result, dict) else None) or result
assert isinstance(op, dict), "schema show missing operation object"
assert "limits" not in op or op.get("limits") is None, "legacy schema must omit limits"
'

section "Published binary: unknown classify op is store-free"
unknown_out="$("$publish_root/tally" classify invoke 2>&1 || true)"
if grep -qiE 'classify\.db|ClassifyStateStore|SELECT ' <<<"$unknown_out"; then
    printf 'unknown classify op leaked storage detail\n' >&2
    exit 1
fi

printf '\nclassify public contract gate: PASS\n'
