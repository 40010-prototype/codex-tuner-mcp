#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root 'src'
$outExe = Join-Path $root 'CodexDesktopTuning.exe'

$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    throw "csc.exe not found at $csc"
}

$sources = @(
    (Join-Path $srcDir 'Models.cs'),
    (Join-Path $srcDir 'VersionDetector.cs'),
    (Join-Path $srcDir 'CacheStore.cs'),
    (Join-Path $srcDir 'AppServerClient.cs'),
    (Join-Path $srcDir 'DesktopUiaController.cs'),
    (Join-Path $srcDir 'McpServer.cs'),
    (Join-Path $srcDir 'Program.cs')
)

$prevExe = Join-Path $root 'CodexDesktopTuning.prev.exe'
if (Test-Path -LiteralPath $prevExe) {
    try { Remove-Item -LiteralPath $prevExe -Force -ErrorAction Stop } catch {}
}
if (Test-Path -LiteralPath $outExe) {
    try {
        [System.IO.File]::Open($outExe, 'Open', 'ReadWrite', 'None').Dispose()
    } catch {
        # Binary is currently running (e.g. MCP server); rename in-place so csc.exe can emit fresh EXE.
        $bakName = "CodexDesktopTuning.prev.$((Get-Date).ToString('HHmmss')).exe"
        Move-Item -LiteralPath $outExe -Destination (Join-Path $root $bakName) -Force
    }
}

$argsList = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/optimize+',
    "/out:$outExe",
    "/reference:$fw\WPF\UIAutomationClient.dll",
    "/reference:$fw\WPF\UIAutomationTypes.dll",
    "/reference:$fw\WPF\WindowsBase.dll",
    "/reference:$fw\System.Windows.Forms.dll",
    "/reference:$fw\System.Web.Extensions.dll",
    "/reference:$fw\System.Core.dll",
    "/reference:$fw\System.Xml.dll",
    "/reference:$fw\System.Xml.Linq.dll"
) + $sources

& $csc @argsList
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

$info = Get-Item -LiteralPath $outExe
Write-Output ("Built: {0} ({1} bytes)" -f $info.FullName, $info.Length)
