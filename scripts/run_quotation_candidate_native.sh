#!/usr/bin/env bash
set -euo pipefail
# Runs only inside the isolated, raw-hash-verified candidate checkout.
available_kib=$(awk '/^MemAvailable:/ { print $2 }' /proc/meminfo)
if [[ ! "$available_kib" =~ ^[0-9]+$ ]] || (( available_kib < 4194304 )); then
  echo '4096 MiB resource admission guard failed.' >&2
  exit 1
fi
export GITHUB_ACTIONS=false
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export MalievWorkspaceRoot="$PWD/.dependencies"
mkdir -p TestResults/CandidateNative
solution=Legacy.Maliev.QuotationService.slnx
# Build first, warnings and errors both fail. Never use prior main binaries.
dotnet restore "$solution" --disable-parallel 2>&1 | tee TestResults/CandidateNative/restore.log
dotnet build "$solution" -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false -warnaserror 2>&1 | tee TestResults/CandidateNative/build.log
mkdir -p TestResults/CandidateNative/Suite
projects=(
  Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj
  tools/HostedStorageFront.Tests/HostedStorageFront.Tests.csproj
  tools/FinancialCompletionEvidence.Tests/FinancialCompletionEvidence.Tests.csproj
  tools/HostedProcessStartObserver.Tests/HostedProcessStartObserver.Tests.csproj
)
for project in "${projects[@]}"; do
  name=$(basename "$project" .csproj)
  dotnet test "$project" -c Release --no-build --no-restore --list-tests 2>&1 | tee "TestResults/CandidateNative/Suite/$name.discovery.log"
  dotnet test "$project" -c Release --no-build --no-restore --results-directory "$PWD/TestResults/CandidateNative/Suite" --logger "trx;LogFileName=$name.trx" 2>&1 | tee "TestResults/CandidateNative/Suite/$name.test.log"
done
python3 -B ../scripts/check_quotation_candidate_native.py --candidate "$PWD" --phase suite
dotnet format "$solution" --verify-no-changes --no-restore 2>&1 | tee TestResults/CandidateNative/format.log
dotnet list "$solution" package --vulnerable --include-transitive --no-restore --format json --output-version 1 > TestResults/CandidateNative/package-audit.json 2> TestResults/CandidateNative/package-audit.stderr.log
python3 -B ../scripts/check_quotation_candidate_native.py --candidate "$PWD" --phase audit
# Prove coverage gate failure and success behavior
python3 -m unittest discover -s scripts/tests -p 'test_*.py'
python3 -B -m unittest discover -s tools/InvoiceCompletionProducerAcceptance/companion -p 'test_*.py'


# Prove actual qualification DTO and controller serializer wire
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj \
  --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
  --filter 'FullyQualifiedName~QualificationOutcomeWireSourceTests' \
  --results-directory TestResults/QualificationWire --logger 'trx;LogFileName=qualification-wire.trx'
python3 -B scripts/check_qualification_wire_results.py
dotnet tools/QualificationOutcomeWireSource/bin/Release/net10.0/QualificationOutcomeWireSource.dll "$PWD"


# Collect QuotationService coverage
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj \
  --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
  --filter 'FullyQualifiedName~QuotationInvoiceCapabilityVerifierTests' \
  --results-directory TestResults/C821Verifier --logger 'trx;LogFileName=c821-verifier.trx'
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj \
  --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
  --filter 'FullyQualifiedName~QuotationInvoiceCapabilityHttpTests' \
  --results-directory TestResults/C821Recipient --logger 'trx;LogFileName=c821-recipient.trx'
python3 scripts/check_c821_focused_results.py
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj \
  --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
  --filter 'FullyQualifiedName~CompanionLauncherAdmissionTests' \
  --results-directory TestResults/CompanionAdmission --logger 'trx;LogFileName=companion-admission.trx'
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj \
  --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
  --filter 'FullyQualifiedName~HostedV4SignedReadVerifierTests' \
  --results-directory TestResults/CompanionSigning --logger 'trx;LogFileName=companion-signing.trx'
python3 -B scripts/check_companion_focused_results.py
dotnet test tools/HostedStorageFront.Tests/HostedStorageFront.Tests.csproj \
  --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
  --results-directory TestResults/StorageFront --logger 'trx;LogFileName=storage-front.trx'
python3 -B scripts/check_storage_front_results.py
dotnet test tools/FinancialCompletionEvidence.Tests/FinancialCompletionEvidence.Tests.csproj \
  --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
  --results-directory TestResults/FinancialCompletion --logger 'trx;LogFileName=financial-completion.trx'
python3 -B scripts/check_financial_completion_results.py
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj \
  --configuration Release --no-build --no-restore \
  -p:GITHUB_ACTIONS=false --collect 'XPlat Code Coverage' --results-directory TestResults/CoverageGate \
  --settings Legacy.Maliev.QuotationService.Tests/coverage.runsettings \
  --logger 'trx;LogFileName=quotation-complete-coverage.trx'


# Enforce 80 percent owned handwritten line coverage
mapfile -t reports < <(find TestResults/CoverageGate -mindepth 2 -maxdepth 2 -type f -name coverage.cobertura.xml)
if [[ "${#reports[@]}" -ne 1 ]]; then
  echo "Expected exactly one Cobertura report, found ${#reports[@]}." >&2
  exit 1
fi
python3 scripts/check_owned_coverage.py "${reports[0]}" --minimum 80
python3 scripts/check_owned_coverage.py "${reports[0]}" --minimum 80 --raw
