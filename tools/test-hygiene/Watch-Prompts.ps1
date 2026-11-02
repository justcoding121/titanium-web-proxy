# Samples visible windows, UAC, and non-loopback listeners while tests run.
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$LogDir,
    [int]$Seconds = 7200
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$log = Join-Path $LogDir 'prompts.jsonl'
if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
$rootNorm = $Root.TrimEnd('\').ToLowerInvariant()

if (-not ([System.Management.Automation.PSTypeName]'TwpPromptWatch').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
public static class TwpPromptWatch {
    public static ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClass, int nMaxCount);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public static void Snapshot() {
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            var title = new StringBuilder(512);
            GetWindowText(h, title, title.Capacity);
            var text = title.ToString();
            if (string.IsNullOrWhiteSpace(text)) return true;
            var cls = new StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            Lines.Enqueue(text + "\t" + cls + "\t" + pid);
            return true;
        }, IntPtr.Zero);
    }
}
"@
}

function Write-Event {
    param([string]$Kind, [string]$Detail)
    $row = [pscustomobject]@{
        time   = (Get-Date).ToUniversalTime().ToString('o')
        kind   = $Kind
        detail = $Detail
    }
    ($row | ConvertTo-Json -Compress) | Add-Content -LiteralPath $log -Encoding UTF8
}

$seenWindows = @{}
$seenListeners = @{}
$deadline = (Get-Date).AddSeconds($Seconds)
$tick = 0
$titlePattern = 'Windows Security Alert|Windows Security|Windows Defender Firewall|Security Warning|Root Certificate Store|User Account Control|Windows protected your PC'
$interestingNames = @('testhost', 'titanium', 'titaniuminspector', 'chrome', 'msedge', 'firefox', 'dotnet')

while ((Get-Date) -lt $deadline) {
    [TwpPromptWatch]::Snapshot()
    while ($true) {
        $line = $null
        if (-not [TwpPromptWatch]::Lines.TryDequeue([ref]$line)) { break }
        $parts = $line.Split("`t")
        if ($parts.Count -lt 3) { continue }
        $title = $parts[0]
        $class = $parts[1]
        $procId = [int]$parts[2]
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        $procName = if ($proc) { $proc.ProcessName } else { '' }
        $path = ''
        try { if ($proc) { $path = $proc.Path } } catch { }
        $dialog = $title -match $titlePattern
        $owned = $false
        $pathLower = ''
        if ($path) { $pathLower = $path.ToLowerInvariant() }
        if ($pathLower.StartsWith($rootNorm)) { $owned = $true }
        $procLower = $procName.ToLowerInvariant()
        # PromptGuard's own fixture window is titled like a firewall alert. It is not an OS dialog.
        if ($procLower -eq 'promptguard') { continue }
        # CredentialUIBroker is the sign-in picker (Windows Hello, smart card). The firewall alert is PickerHost.
        if ($procLower -eq 'credentialuibroker') { continue }
        if ($procLower -eq 'testhost' -or $procLower -like 'titanium*') { $owned = $true }
        # The user's own browser is not a test window. Playwright browsers live under the test root or ms-playwright.
        if (($procLower -eq 'chrome' -or $procLower -eq 'msedge' -or $procLower -eq 'firefox') -and
            ($pathLower.Contains('ms-playwright') -or $pathLower.StartsWith($rootNorm))) { $owned = $true }
        if ($procLower -eq 'dotnet' -and $pathLower.StartsWith($rootNorm)) { $owned = $true }
        if (-not $dialog -and -not $owned) { continue }
        $key = "$title|$class|$procId"
        if ($seenWindows.ContainsKey($key)) { continue }
        $seenWindows[$key] = $true
        Write-Event -Kind 'window' -Detail ("title={0}; class={1}; pid={2}; process={3}; path={4}" -f $title, $class, $procId, $procName, $path)
    }

    $consent = Get-Process -Name consent -ErrorAction SilentlyContinue
    if ($consent) {
        Write-Event -Kind 'UAC_PENDING' -Detail ("pid={0}" -f ($consent.Id -join ','))
    }

    if (($tick % 4) -eq 0) {
        $endpoints = @()
        $endpoints += @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue)
        $endpoints += @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue)
        foreach ($ep in $endpoints) {
            $addr = [string]$ep.LocalAddress
            if ($addr -eq '127.0.0.1' -or $addr -eq '::1') { continue }
            $owner = Get-Process -Id $ep.OwningProcess -ErrorAction SilentlyContinue
            if (-not $owner) { continue }
            $ownerPath = ''
            try { $ownerPath = $owner.Path } catch { }
            $cmd = ''
            $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$($ep.OwningProcess)" -ErrorAction SilentlyContinue
            if ($cim) { $cmd = [string]$cim.CommandLine; if (-not $ownerPath) { $ownerPath = [string]$cim.ExecutablePath } }
            $related = $false
            if ($ownerPath -and $ownerPath.ToLowerInvariant().StartsWith($rootNorm)) { $related = $true }
            $name = $owner.ProcessName.ToLowerInvariant()
            if ($name -eq 'testhost' -or $name -like 'titanium*' -or $name -eq 'titaniuminspector') { $related = $true }
            if ($name -eq 'dotnet' -and ($cmd.ToLowerInvariant().Contains('testhost') -or $cmd.ToLowerInvariant().Contains($rootNorm))) { $related = $true }
            if (-not $related) { continue }
            $key = "{0}|{1}|{2}" -f $ownerPath, $addr, $ep.LocalPort
            if ($seenListeners.ContainsKey($key)) { continue }
            $seenListeners[$key] = $true
            Write-Event -Kind 'listener' -Detail ("process={0}; path={1}; address={2}; port={3}" -f $owner.ProcessName, $ownerPath, $addr, $ep.LocalPort)
        }
    }
    $tick++
    Start-Sleep -Milliseconds 250
}
