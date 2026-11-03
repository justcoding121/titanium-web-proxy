# Logs visible top-level window titles while another command runs.
param(
    [Parameter(Mandatory = $true)][string]$LogPath,
    [int]$Seconds = 900
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Continue'

if (-not ([System.Management.Automation.PSTypeName]'TwpWinWatch').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
public static class TwpWinWatch {
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
            Lines.Enqueue(DateTime.UtcNow.ToString("o") + "\t" + text + "\tpid=" + pid + "\tclass=" + cls);
            return true;
        }, IntPtr.Zero);
    }
}
"@
}

$dir = Split-Path -Parent $LogPath
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$deadline = (Get-Date).AddSeconds($Seconds)
$seen = @{}
while ((Get-Date) -lt $deadline) {
    [TwpWinWatch]::Snapshot()
    while ($true) {
        $line = $null
        if (-not [TwpWinWatch]::Lines.TryDequeue([ref]$line)) { break }
        if ($seen.ContainsKey($line.Substring(30))) { continue }
        $seen[$line.Substring(30)] = $true
        Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    }
    $consent = Get-Process -Name consent -ErrorAction SilentlyContinue
    if ($consent) {
        Add-Content -LiteralPath $LogPath -Value ("{0}`tUAC_PENDING" -f (Get-Date).ToUniversalTime().ToString('o'))
    }
    Start-Sleep -Milliseconds 400
}
