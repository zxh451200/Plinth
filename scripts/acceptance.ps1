param([switch]$Package, [string]$BrandingFile, [switch]$EnableXlsx, [string]$PythonExecutable, [string]$SupplementalComplianceFile)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/python.ps1"
$pythonOptions = @{}
if ($PSBoundParameters.ContainsKey('PythonExecutable')) { $pythonOptions.PythonExecutable = $PythonExecutable }
$python = Resolve-PlinthPython @pythonOptions
if ($BrandingFile) { $BrandingFile = (Resolve-Path -LiteralPath $BrandingFile).Path }
$supplementalOptions = @{}
if ($PSBoundParameters.ContainsKey('SupplementalComplianceFile')) {
    if (-not $Package) { throw 'SupplementalComplianceFile requires -Package' }
    if ([string]::IsNullOrWhiteSpace($SupplementalComplianceFile)) { throw 'Supplemental compliance file must not be empty' }
    $supplementalOptions.SupplementalComplianceFile = (Resolve-Path -LiteralPath $SupplementalComplianceFile).Path
}
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $pythonArguments = @('-m', 'unittest', 'discover', '-s', 'scripts', '-p', 'test_*.py')
    & $python @pythonArguments
    if ($LASTEXITCODE -ne 0) { throw 'Tool tests failed' }
    $pythonArguments = @('scripts/check_source_contracts.py')
    & $python @pythonArguments
    if ($LASTEXITCODE -ne 0) { throw 'Source contracts failed' }
    $pythonArguments = @('scripts/integration_checks.py')
    & $python @pythonArguments
    if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed' }
    $pythonArguments = @('tests/sqlite_migration_smoke.py')
    & $python @pythonArguments
    if ($LASTEXITCODE -ne 0) { throw 'SQLite smoke failed' }
    $pythonArguments = @('scripts/release_checks.py', 'locks')
    & $python @pythonArguments
    if ($LASTEXITCODE -ne 0) { throw 'Missing dependency locks' }
    dotnet --info
    if ($LASTEXITCODE -ne 0) { throw 'SDK unavailable' }
    dotnet restore Plinth.sln --locked-mode --configfile scripts/nuget.config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet format Plinth.sln --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Format failed' }
    dotnet build Plinth.sln -c Release --no-restore -warnaserror:nullable
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $testResultsRoot = Join-Path (Get-Location) 'artifacts/test-results'
    $testProjects = @(
        'tests/Plinth.App.Tests/Plinth.App.Tests.csproj',
        'tests/Plinth.Spreadsheet.Tests/Plinth.Spreadsheet.Tests.csproj'
    )
    foreach ($testProject in $testProjects) {
        $testName = [System.IO.Path]::GetFileNameWithoutExtension($testProject)
        $resultsDirectory = Join-Path $testResultsRoot $testName
        dotnet test $testProject -c Release --no-build --no-restore -p:TestingPlatformDotnetTestSupport=true -- --results-directory $resultsDirectory --report-xunit-trx --report-xunit-trx-filename "${testName}.trx"
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    if ($EnableXlsx) {
        $xlsxResultsDirectory = Join-Path (Get-Location) 'artifacts/test-results-xlsx'
        dotnet test tests/Plinth.App.Tests/Plinth.App.Tests.csproj -c Release --no-restore -p:PlinthEnableXlsx=true -p:TestingPlatformDotnetTestSupport=true -- --results-directory $xlsxResultsDirectory --report-xunit-trx --report-xunit-trx-filename xlsx.trx --filter-class "*Spreadsheet*"
        if ($LASTEXITCODE -ne 0) { throw 'XLSX page tests failed' }
    }
    if ($Package) {
        & "$PSScriptRoot/publish.ps1" -BrandingFile $BrandingFile -RequireInstaller -EnableXlsx:$EnableXlsx -PythonExecutable $python @supplementalOptions
    }
}
finally { Pop-Location }
