<#
.SYNOPSIS
  Build a self-contained Windows candidate, optional branding, notices and SHA256.
  Requires real committed dependency locks; does not publish to an external service.
#>
param(
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$BrandingFile,
    [switch]$RequireInstaller,
    [switch]$EnableXlsx,
    [string]$PythonExecutable,
    [string]$SupplementalComplianceFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/python.ps1"
$pythonOptions = @{}
if ($PSBoundParameters.ContainsKey('PythonExecutable')) { $pythonOptions.PythonExecutable = $PythonExecutable }
$python = Resolve-PlinthPython @pythonOptions
$root = Split-Path $PSScriptRoot -Parent
$xlsxProperty = "-p:PlinthEnableXlsx=$($EnableXlsx.IsPresent.ToString().ToLowerInvariant())"
# Resolve caller-supplied relative paths before changing location.
if ($PSBoundParameters.ContainsKey('SupplementalComplianceFile')) {
    if ([string]::IsNullOrWhiteSpace($SupplementalComplianceFile)) { throw 'Supplemental compliance file must not be empty' }
    $SupplementalComplianceFile = (Resolve-Path -LiteralPath $SupplementalComplianceFile).Path
    if (-not (Test-Path -LiteralPath $SupplementalComplianceFile -PathType Leaf)) { throw 'Supplemental compliance input must be a file' }
    # Validate explicit evidence before restore/build or replacing an existing candidate.
    & $python "$PSScriptRoot/supplemental_compliance.py" --validate $SupplementalComplianceFile
    if ($LASTEXITCODE -ne 0) { throw 'Supplemental compliance validation failed' }
}
if ($BrandingFile) {
    $BrandingFile = (Resolve-Path -LiteralPath $BrandingFile).Path
    if (-not (Test-Path -LiteralPath $BrandingFile -PathType Leaf)) { throw 'Branding must be a file' }
    $brandConfig = Get-Content -LiteralPath $BrandingFile -Raw | ConvertFrom-Json
}
Push-Location $root
try {
    if ($RequireInstaller -and -not (Get-Command makensis -ErrorAction SilentlyContinue)) {
        throw 'NSIS makensis is required'
    }
    $pythonArguments = @('scripts/release_checks.py', 'locks')
    & $python @pythonArguments
    if ($LASTEXITCODE -ne 0) { throw 'Real dependency locks required' }
    $version = (Select-Xml -Path 'Directory.Build.props' -XPath '//Version').Node.InnerText
    $publishDir = Join-Path $root "artifacts/publish/$Runtime"
    $complianceDir = Join-Path $root 'artifacts/compliance'
    # Both the solution and RID-specific publish must obey the committed locks.
    dotnet restore Plinth.sln --locked-mode --configfile scripts/nuget.config
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
    if (Test-Path $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
    dotnet restore src/Plinth.App/Plinth.App.csproj -r $Runtime --locked-mode --configfile scripts/nuget.config
    if ($LASTEXITCODE -ne 0) { throw 'RID locked restore failed; regenerate and review real runtime-specific locks' }
    dotnet publish src/Plinth.App/Plinth.App.csproj -c Release -r $Runtime --self-contained true -o $publishDir --no-restore -p:PublishTrimmed=false -p:PublishAot=false $xlsxProperty
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    $closedXml = Join-Path $publishDir 'ClosedXML.dll'
    if ($EnableXlsx.IsPresent -and -not (Test-Path -LiteralPath $closedXml)) {
        throw 'XLSX enabled but ClosedXML.dll is missing from publish'
    }
    if (-not $EnableXlsx.IsPresent -and (Test-Path -LiteralPath $closedXml)) {
        throw 'ClosedXML.dll must not be published when XLSX is disabled'
    }
    $pythonArguments = @('scripts/third_party.py', '--assets', 'src/Plinth.App/obj/project.assets.json', '--output', $complianceDir, '--publish-directory', $publishDir)
    if ($PSBoundParameters.ContainsKey('SupplementalComplianceFile')) {
        $pythonArguments += @('--supplemental-compliance-file', $SupplementalComplianceFile)
    }
    & $python @pythonArguments
    if ($LASTEXITCODE -ne 0) { throw 'License/notice/SBOM generation failed' }
    Copy-Item "$complianceDir/THIRD_PARTY_NOTICES.md", "$complianceDir/sbom.cdx.json", "$complianceDir/coverage-report.json" -Destination $publishDir
    if ($BrandingFile) {
        Copy-Item -LiteralPath $BrandingFile -Destination "$publishDir/branding.json"
        foreach ($asset in @(@('LogoFile', 'png'), @('IconFile', 'ico'))) {
            $property = $brandConfig.PSObject.Properties[$asset[0]]
            if ($null -eq $property -or [string]::IsNullOrEmpty([string]$property.Value)) { continue }
            $name = [string]$property.Value
            if ($name -notmatch "^[A-Za-z0-9][A-Za-z0-9_-]*\.$($asset[1])\z" -or $name.Length -gt 100) {
                throw "Invalid branding asset name: $($asset[0])"
            }
            $source = Join-Path (Split-Path $BrandingFile -Parent) $name
            if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -gt 2MB) {
                throw "Missing or oversized branding asset: $($asset[0])"
            }
            Copy-Item -LiteralPath $source -Destination (Join-Path $publishDir $name)
        }
    }
    $zipPath = Join-Path $root "artifacts/Plinth-$version-$Runtime.zip"
    if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath
    $outputs = @($zipPath)
    if (Get-Command makensis -ErrorAction SilentlyContinue) {
        $setupPath = Join-Path $root "artifacts/Plinth-Setup-$version-$Runtime.exe"
        $installerManifest = Join-Path $complianceDir 'installer-files.nsh'
        $pythonArguments = @('scripts/installer_manifest.py', '--publish', $publishDir, '--output', $installerManifest)
        & $python @pythonArguments
        if ($LASTEXITCODE -ne 0) { throw 'Installer file manifest generation failed' }
        if (Test-Path $setupPath) { Remove-Item -LiteralPath $setupPath -Force }
        makensis "/DVERSION=$version" "/DPUBLISH_DIR=$publishDir" "/DOUTPUT_FILE=$setupPath" "/DUNINSTALL_MANIFEST=$installerManifest" installer/plinth.nsi
        if ($LASTEXITCODE -ne 0) { throw 'makensis failed' }
        $outputs += $setupPath
    }
    else { Write-Host 'NSIS unavailable: installer NOT EXECUTED (use -RequireInstaller for a hard gate)' }
    foreach ($file in $outputs) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText("$file.sha256", "$hash  $([IO.Path]::GetFileName($file))`n", (New-Object System.Text.UTF8Encoding($false)))
        $pythonArguments = @('scripts/release_checks.py', 'package', $file)
        & $python @pythonArguments
        if ($LASTEXITCODE -ne 0) { throw "Artifact verification failed: $file" }
    }
    Write-Host 'Candidate packaged. Installation/upgrade/uninstall and non-NuGet license review remain manual gates.'
}
finally { Pop-Location }
