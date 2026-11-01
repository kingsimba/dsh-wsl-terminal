# Build the native terminal launcher with the .NET Framework C# compiler that
# ships with Windows. Run from any directory:
#   powershell -ExecutionPolicy Bypass -File build.ps1
param(
    [string]$Output = (Join-Path $PSScriptRoot 'dsh-terminal-shell.exe')
)

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "C# compiler not found at $csc"
}

& $csc /nologo /target:exe /platform:x64 /optimize+ /reference:System.Web.Extensions.dll "/out:$Output" (Join-Path $PSScriptRoot 'DshTerminalShell.cs')
if ($LASTEXITCODE -ne 0) {
    throw "csc failed with exit code $LASTEXITCODE"
}
Write-Host "Built $Output"
