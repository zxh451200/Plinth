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
    dotnet test Plinth.sln -c Release --no-build --no-restore -p:TestingPlatformDotnetTestSupport=true --logger trx --results-directory artifacts/test-results
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    if ($EnableXlsx) {
        dotnet test tests/Plinth.App.Tests/Plinth.App.Tests.csproj -c Release --no-restore -p:PlinthEnableXlsx=true -p:TestingPlatformDotnetTestSupport=true --filter FullyQualifiedName~Spreadsheet --logger trx --results-directory artifacts/test-results-xlsx
        if ($LASTEXITCODE -ne 0) { throw 'XLSX page tests failed' }
    }
    if ($Package) {
        & "$PSScriptRoot/publish.ps1" -BrandingFile $BrandingFile -RequireInstaller -EnableXlsx:$EnableXlsx -PythonExecutable $python @supplementalOptions
    }
}
finally { Pop-Location }
