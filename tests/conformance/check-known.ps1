# Compares a conformance report with an allow-list of known gaps and fails on anything new.
#
#   pwsh tests/conformance/check-known.ps1 -Kind h2spec   -Report h2spec-report.xml -Known tests/conformance/h2spec-known.txt
#   pwsh tests/conformance/check-known.ps1 -Kind autobahn -Report reports/clients/index.json -Known tests/conformance/autobahn-known.txt
#
# Allow-list format: one entry per line, '#' starts a comment (see README.md).
#   h2spec    an entry is a substring of "<package> | <classname> | <name>" of a failing JUnit test case, so
#             "http2/6.5.3" or a section title or a full test name all work.
#   autobahn  an entry is a case id such as "6.4.3" ("6.4" does not match "6.4.3": ids match exactly).
# A failure that no entry covers fails the run. An entry that matches nothing is reported as stale (not a failure).
# Exit codes: 0 clean, 1 unexpected failure(s), 2 unusable report.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('h2spec', 'autobahn')][string] $Kind,
    [Parameter(Mandatory)][string] $Report,
    [Parameter(Mandatory)][string] $Known
)

$ErrorActionPreference = 'Stop'

function Get-KnownEntries([string] $path) {
    if (-not (Test-Path $path)) { return @() }
    return @(Get-Content $path | ForEach-Object { ($_ -replace '#.*$', '').Trim() } | Where-Object { $_ })
}

if (-not (Test-Path $Report)) {
    Write-Host "::error::No $Kind report at $Report"
    exit 2
}

$allowed = @(Get-KnownEntries $Known)
$failures = New-Object System.Collections.Generic.List[string]
$total = 0

if ($Kind -eq 'h2spec') {
    [xml] $xml = Get-Content $Report -Raw
    $cases = @($xml.SelectNodes('//testcase'))
    $total = $cases.Count
    foreach ($case in $cases) {
        $failed = $case.SelectNodes('failure|error')
        if ($failed.Count -eq 0) { continue }
        $failures.Add(('{0} | {1} | {2}' -f $case.GetAttribute('package'), $case.GetAttribute('classname'), $case.GetAttribute('name')))
    }
}
else {
    $index = Get-Content $Report -Raw | ConvertFrom-Json
    foreach ($agent in $index.PSObject.Properties) {
        foreach ($case in $agent.Value.PSObject.Properties) {
            $total++
            $bad = @($case.Value.behavior, $case.Value.behaviorClose) | Where-Object { $_ -eq 'FAILED' }
            if ($bad) { $failures.Add($case.Name) }
        }
    }
}

if ($total -eq 0) {
    Write-Host "::error::The $Kind report at $Report has no test cases"
    exit 2
}

$used = New-Object System.Collections.Generic.HashSet[string]
$unexpected = @()
foreach ($f in $failures) {
    $hit = $null
    foreach ($k in $allowed) {
        $match = if ($Kind -eq 'autobahn') { $f -eq $k } else { $f.Contains($k) }
        if ($match) { $hit = $k; break }
    }
    if ($hit) { [void] $used.Add($hit) } else { $unexpected += $f }
}

Write-Host ("{0}: {1} case(s), {2} failing, {3} known, {4} unexpected" -f $Kind, $total, $failures.Count, ($failures.Count - $unexpected.Count), $unexpected.Count)
foreach ($k in $allowed) {
    if (-not $used.Contains($k)) { Write-Host "::notice::Stale allow-list entry (no longer failing): $k" }
}
if ($unexpected.Count -gt 0) {
    foreach ($u in $unexpected) { Write-Host "::error::Unexpected $Kind failure: $u" }
    exit 1
}
exit 0
