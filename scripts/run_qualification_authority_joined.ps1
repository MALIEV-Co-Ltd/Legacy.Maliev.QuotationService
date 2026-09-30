param([string]$RepositoryPath = (Join-Path $PSScriptRoot '..'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path -LiteralPath $RepositoryPath).Path
$auth = Join-Path $root 'TestResults/.joined-auth/Legacy.Maliev.AuthService'
$authDependencies = Join-Path $root 'TestResults/.joined-auth/.dependencies'
$quotationDependencies = Join-Path $root 'TestResults/.bridge-dependencies'

function Assert-Pin([string]$Path, [string]$Revision) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw 'Required isolated checkout is absent.' }
    $head = & git -C $Path rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -cne $Revision) { throw 'Required isolated checkout revision is incorrect.' }
    $status = & git -C $Path status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $status) { throw 'Required isolated checkout is not clean.' }
}

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Joined qualification validation command failed.' }
}

Assert-Pin $auth '8cdb634b3b0abdf18b9b826a0948dbfd98c66ea0'
Assert-Pin (Join-Path $authDependencies 'Legacy.Maliev.ServiceDefaults') '5c5f9479313710fa576f83d3b396442997a2fcf4'
Assert-Pin (Join-Path $authDependencies 'Legacy.Maliev.CompatibilityContracts') '78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7'
Assert-Pin (Join-Path $quotationDependencies 'Legacy.Maliev.ServiceDefaults') '8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3'
Assert-Pin (Join-Path $quotationDependencies 'Legacy.Maliev.CompatibilityContracts') '78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7'

$previousActions = [Environment]::GetEnvironmentVariable('GITHUB_ACTIONS')
Push-Location $root
try {
    $env:GITHUB_ACTIONS = 'false'
    $authProperties = @('-p:UseLocalMalievDependencies=true', "-p:MalievWorkspaceRoot=$authDependencies")
    $quotationProperties = @('-p:UseLocalMalievDependencies=true', "-p:MalievWorkspaceRoot=$quotationDependencies")
    Invoke-Dotnet (@('build', (Join-Path $auth 'Legacy.Maliev.AuthService.Api/Legacy.Maliev.AuthService.Api.csproj'), '-c', 'Release', '-warnaserror') + $authProperties)
    Invoke-Dotnet (@('build', 'tools/QualificationAuthorityAuthFixture/QualificationAuthorityAuthFixture.csproj', '-c', 'Release', '-warnaserror', "-p:QualificationAuthSourceRoot=$auth") + $authProperties)
    Invoke-Dotnet (@('build', 'tools/QualificationAuthorityJoinedTests/QualificationAuthorityJoinedTests.csproj', '-c', 'Release', '-warnaserror') + $quotationProperties)
    $results = Join-Path $root ('TestResults/bridge-joined-script/' + [Guid]::NewGuid().ToString('N'))
    Invoke-Dotnet (@('test', 'tools/QualificationAuthorityJoinedTests/QualificationAuthorityJoinedTests.csproj', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx', '--results-directory', $results, '--blame-hang-timeout', '8m', '--blame-hang-dump-type', 'none') + $quotationProperties)
    $reports = @(Get-ChildItem -LiteralPath $results -Filter '*.trx' -File)
    if ($reports.Count -ne 1) { throw 'Joined qualification validation requires exactly one TRX report.' }
    [xml]$report = Get-Content -LiteralPath $reports[0].FullName -Raw
    $counts = $report.TestRun.ResultSummary.Counters
    if ([int]$counts.total -ne 3 -or [int]$counts.executed -ne 3 -or [int]$counts.passed -ne 3 -or
        [int]$counts.failed -ne 0 -or [int]$counts.notExecuted -ne 0) {
        throw 'Joined qualification validation requires exactly three passing, executed tests and zero skips.'
    }
    Write-Output 'Joined qualification authority: exact isolated pins, clean source checkouts, build-first and 3/3 tests PASS.'
} finally {
    [Environment]::SetEnvironmentVariable('GITHUB_ACTIONS', $previousActions)
    Pop-Location
}
