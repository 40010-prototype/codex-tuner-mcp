#Requires -Version 5.1
<#
Zero-Add-Type PowerShell wrapper for CodexDesktopTuning.exe.
Delegates CLI invocations to the compiled C# binary without dynamic %TEMP% compilation.
#>
param(
    [ValidateSet('query', 'set-model', 'set-effort', 'set', 'models', 'doctor', 'capabilities', 'set-preference', 'create-thread', 'help')]
    [string]$Action = 'query',
    [string]$ThreadId,
    [string]$Cwd,
    [string]$Title,
    [string]$Value,
    [string]$Model,
    [string]$Effort,
    [ValidateSet('backend', 'auto', 'ui')]
    [string]$Source = 'backend',
    [ValidateSet('', 'restore', 'stay', 'focus_codex')]
    [string]$FocusMode = '',
    [switch]$NoForegroundFallback,
    [switch]$AllowSessionSwitch,
    [switch]$NoUnminimize,
    [switch]$IgnoreUserIdle,
    [switch]$Refresh,
    [switch]$ResetBreaker,
    [switch]$Pretty,
    [int]$TtlHours = 0,
    [int]$LockTimeoutMs = 0
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'CodexDesktopTuning.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    [ordered]@{
        ok = $false
        command = $Action
        error = 'binary_not_built'
        message = "CodexDesktopTuning.exe not found at $exe. Run .\build.ps1 first."
        mayHaveChanged = $false
    } | ConvertTo-Json -Compress
    exit 1
}

$cliArgs = [System.Collections.Generic.List[string]]::new()
$cliArgs.Add($Action)

if ($ThreadId) { $cliArgs.Add('--thread-id'); $cliArgs.Add($ThreadId) }
if ($Cwd) { $cliArgs.Add('--cwd'); $cliArgs.Add($Cwd) }
if ($Title) { $cliArgs.Add('--title'); $cliArgs.Add($Title) }
if ($Value) { $cliArgs.Add('--value'); $cliArgs.Add($Value) }
if ($Model) { $cliArgs.Add('--model'); $cliArgs.Add($Model) }
if ($Effort) { $cliArgs.Add('--effort'); $cliArgs.Add($Effort) }
if ($Action -ceq 'query' -and $Source -cne 'backend') { $cliArgs.Add('--source'); $cliArgs.Add($Source) }
if ($FocusMode) { $cliArgs.Add('--focus-mode'); $cliArgs.Add($FocusMode) }
if ($NoForegroundFallback) { $cliArgs.Add('--no-foreground-fallback') }
if ($AllowSessionSwitch) { $cliArgs.Add('--allow-session-switch') }
if ($NoUnminimize) { $cliArgs.Add('--no-unminimize') }
if ($IgnoreUserIdle) { $cliArgs.Add('--ignore-user-idle') }
if ($Refresh) { $cliArgs.Add('--refresh') }
if ($ResetBreaker) { $cliArgs.Add('--reset-breaker') }
if ($Pretty) { $cliArgs.Add('--pretty') }
if ($TtlHours -gt 0) { $cliArgs.Add('--ttl-hours'); $cliArgs.Add($TtlHours.ToString()) }
if ($LockTimeoutMs -gt 0) { $cliArgs.Add('--lock-timeout-ms'); $cliArgs.Add($LockTimeoutMs.ToString()) }

& $exe @cliArgs
exit $LASTEXITCODE
