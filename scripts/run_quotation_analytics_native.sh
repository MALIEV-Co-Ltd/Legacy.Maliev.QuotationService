#!/usr/bin/env bash
set -euo pipefail
# Dedicated analytics scope; historical wire runners and policies remain unchanged.
policy=scripts/quotation-analytics-policy.json
transport="$GITHUB_WORKSPACE"
python3 -B "$transport/scripts/materialize_quotation_candidate.py" --policy "$transport/$policy" --candidate "$PWD" --verify-only
available_kib=$(awk '/^MemAvailable:/ { print $2 }' /proc/meminfo)
if [[ ! "$available_kib" =~ ^[0-9]+$ ]] || (( available_kib < 4194304 )); then
  echo '4096 MiB resource admission guard failed.' >&2; exit 1
fi
if pgrep -f '(^|/)(dotnet|testhost|VBCSCompiler)( |$)' >/dev/null; then
  echo 'Existing SDK/test worker blocks native allocation.' >&2; exit 1
fi
export GITHUB_ACTIONS=false DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export MalievWorkspaceRoot="$PWD/.dependencies"
solution=Legacy.Maliev.QuotationService.slnx
mkdir -p TestResults/CandidateNative/{Analytics,Suite}
timeout --signal=TERM --kill-after=20s 300s dotnet restore "$solution" --disable-parallel 2>&1 | tee TestResults/CandidateNative/restore.log
timeout --signal=TERM --kill-after=20s 300s dotnet build "$solution" -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false -p:ShouldUnsetParentConfigurationAndPlatform=false -warnaserror 2>&1 | tee TestResults/CandidateNative/build.log
project=Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj
filter=FullyQualifiedName~Legacy.Maliev.QuotationService.Tests.Analytics.QuotationAnalyticsRetryContractTests
timeout --signal=TERM --kill-after=20s 180s dotnet test "$project" -c Release --no-build --no-restore --filter "$filter" --list-tests 2>&1 | tee TestResults/CandidateNative/Analytics/discovery.log
timeout --signal=TERM --kill-after=20s 180s dotnet test "$project" -c Release --no-build --no-restore --filter "$filter" --results-directory "$PWD/TestResults/CandidateNative/Analytics" --logger 'trx;LogFileName=analytics.trx' 2>&1 | tee TestResults/CandidateNative/Analytics/test.log
python3 -B "$transport/scripts/check_quotation_analytics_results.py" --candidate "$PWD"
projects=(
  Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj
  tools/HostedStorageFront.Tests/HostedStorageFront.Tests.csproj
  tools/FinancialCompletionEvidence.Tests/FinancialCompletionEvidence.Tests.csproj
  tools/HostedProcessStartObserver.Tests/HostedProcessStartObserver.Tests.csproj
)
for project in "${projects[@]}"; do
  name=$(basename "$project" .csproj)
  timeout --signal=TERM --kill-after=20s 180s dotnet test "$project" -c Release --no-build --no-restore --list-tests 2>&1 | tee "TestResults/CandidateNative/Suite/$name.discovery.log"
  timeout --signal=TERM --kill-after=20s 480s dotnet test "$project" -c Release --no-build --no-restore --results-directory "$PWD/TestResults/CandidateNative/Suite" --logger "trx;LogFileName=$name.trx" 2>&1 | tee "TestResults/CandidateNative/Suite/$name.test.log"
done
python3 -B "$transport/scripts/check_quotation_candidate_native.py" --candidate "$PWD" --phase suite
timeout --signal=TERM --kill-after=20s 180s dotnet format "$solution" --verify-no-changes --no-restore 2>&1 | tee TestResults/CandidateNative/format.log
timeout --signal=TERM --kill-after=20s 120s dotnet list "$solution" package --include-transitive --no-restore --format json --output-version 1 > TestResults/CandidateNative/package-graph.json 2> TestResults/CandidateNative/package-graph.stderr.log
timeout --signal=TERM --kill-after=20s 120s dotnet list "$solution" package --vulnerable --include-transitive --no-restore --format json --output-version 1 > TestResults/CandidateNative/package-audit.json 2> TestResults/CandidateNative/package-audit.stderr.log
python3 -B "$transport/scripts/check_quotation_candidate_native.py" --candidate "$PWD" --phase audit
timeout --signal=TERM --kill-after=20s 180s python3 -B -m unittest discover -s scripts/tests -p 'test_*.py'
timeout --signal=TERM --kill-after=20s 180s python3 -B -m unittest discover -s tools/InvoiceCompletionProducerAcceptance/companion -p 'test_*.py'
project=Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj
timeout --signal=TERM --kill-after=20s 900s dotnet test "$project" -c Release --no-build --no-restore --collect 'XPlat Code Coverage' --settings Legacy.Maliev.QuotationService.Tests/coverage.runsettings --results-directory "$PWD/TestResults/CandidateNative/Coverage" --logger 'trx;LogFileName=quotation.trx' 2>&1 | tee TestResults/CandidateNative/coverage.log
python3 -B "$transport/scripts/check_quotation_analytics_results.py" --candidate "$PWD" --phase coverage
mapfile -t reports < <(find TestResults/CandidateNative/Coverage -mindepth 2 -maxdepth 2 -type f -name coverage.cobertura.xml)
if [[ "${#reports[@]}" -ne 1 ]]; then
  echo 'Exactly one original Cobertura report is required.' >&2; exit 1
fi
python3 -B scripts/check_owned_coverage.py "${reports[0]}" --minimum 80
python3 -B scripts/check_owned_coverage.py "${reports[0]}" --minimum 80 --raw
python3 -B "$transport/scripts/materialize_quotation_candidate.py" --policy "$transport/$policy" --candidate "$PWD" --verify-only
