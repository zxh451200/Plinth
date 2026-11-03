# Resolve once, then invoke the returned executable directly with argument arrays.
function Resolve-PlinthPython {
    [CmdletBinding()]
    param([string]$PythonExecutable)

    $failure = 'Python 3.8 or newer is required. No validated interpreter was found. Install Python >=3.8 or pass -PythonExecutable with its executable path. An invalid explicit override will not fall back.'
    $probe = 'import sys; sys.exit(1) if sys.version_info < (3, 8) else None; print(sys.executable)'
    if ($PSBoundParameters.ContainsKey('PythonExecutable')) {
        if ([string]::IsNullOrWhiteSpace($PythonExecutable)) { throw $failure }
        $candidates = @(@{ Name = $PythonExecutable; Arguments = @() })
    }
    else {
        $candidates = @(
            @{ Name = 'python'; Arguments = @() }
            @{ Name = 'python3'; Arguments = @() }
        )
        if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
            $candidates += @{ Name = 'py'; Arguments = @('-3') }
        }
    }

    foreach ($candidate in $candidates) {
        try {
            $command = Get-Command -Name $candidate.Name -CommandType Application -ErrorAction Stop |
                Select-Object -First 1
            $probeArguments = @($candidate.Arguments) + @('-c', $probe)
            $reported = @(& $command.Source @probeArguments 2>$null)
            if ($LASTEXITCODE -ne 0 -or $reported.Count -ne 1) { continue }
            $executable = [string]$reported[0]
            if (-not [IO.Path]::IsPathRooted($executable) -or
                -not (Test-Path -LiteralPath $executable -PathType Leaf)) { continue }

            # Pin the interpreter selected by launchers (including Windows py -3),
            # and verify it directly before handing it to either entry point.
            $directArguments = @('-c', $probe)
            $verified = @(& $executable @directArguments 2>$null)
            if ($LASTEXITCODE -eq 0 -and $verified.Count -eq 1 -and
                [string]$verified[0] -eq $executable) {
                return $executable
            }
        }
        catch {
            # Missing commands, broken launchers and native probe errors all
            # produce the same actionable failure if no candidate succeeds.
        }
    }
    throw $failure
}
