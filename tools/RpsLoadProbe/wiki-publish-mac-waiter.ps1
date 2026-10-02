# Serial Mac Apple Silicon dispatcher for wiki refresh.
# Dispatches one macos-15 shard at a time with API retries, tracks run IDs in a JSON map,
# and treats missing CSV / INCOMPLETE marker as "re-dispatch".
#
# Example (from repo root):
#   pwsh tools/RpsLoadProbe/wiki-publish-mac-waiter.ps1 `
#     -MapPath C:\Work\Repositories\twp-ab\wiki-publish-run-map.json `
#     -Ref develop -MaxConcurrent 1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $MapPath,
    [string] $Repo = 'justcoding121/titanium-web-proxy',
    [string] $Ref = 'develop',
    [string] $LogPath = '',
    [int] $PollSeconds = 120,
    [int] $MaxConcurrent = 1,
    [switch] $DryRun,
    [switch] $ResetMacSlots
)

$ErrorActionPreference = 'Stop'

if (-not $LogPath) {
    $LogPath = Join-Path (Split-Path -Parent $MapPath) 'wiki-publish-waiter.log'
}

function Write-Log([string] $msg) {
    $line = "{0:yyyy-MM-dd HH:mm:ss}Z {1}" -f (Get-Date).ToUniversalTime(), $msg
    Add-Content -LiteralPath $LogPath -Value $line
    Write-Host $line
}

function Invoke-GhJson {
    param(
        [Parameter(Mandatory)][string[]] $GhArgs,
        [int] $Retries = 6,
        [switch] $AllowEmpty
    )
    $delay = 5
    for ($i = 1; $i -le $Retries; $i++) {
        try {
            $raw = & gh @GhArgs 2>&1
            if ($LASTEXITCODE -ne 0) { throw ($raw | Out-String).Trim() }
            $text = if ($null -eq $raw) { '' } else { ($raw | Out-String).Trim() }
            if (-not $text) {
                if ($AllowEmpty) { return $null }
                return $null
            }
            return $text | ConvertFrom-Json
        }
        catch {
            if ($i -eq $Retries) { throw }
            Write-Log "API retry $i/$Retries ($($GhArgs -join ' ')): $($_.Exception.Message)"
            Start-Sleep -Seconds $delay
            $delay = [Math]::Min(60, $delay * 2)
        }
    }
}

# Mac-specific finer shards (PERF-GATES.md). Win/Linux keep 3/2/3/2.
$MacShardPlans = [ordered]@{
    product     = @{ Mode = 'compare-product';     Count = 9 }
    bodies      = @{ Mode = 'compare-bodies';      Count = 4 }
    arch        = @{ Mode = 'compare-arch';        Count = 6 }
    grpc        = @{ Mode = 'compare-grpc';        Count = 4 }
    post        = @{ Mode = 'compare-post';        Count = 2 }
    lossy       = @{ Mode = 'compare-lossy';       Count = 2 }
    tls         = @{ Mode = 'compare-tls-cost';    Count = 2 }
    saturation  = @{ Mode = 'compare-saturation';  Count = 1 }
    ws_h1tls    = @{ Mode = 'compare-ws-h1tls';    Count = 1 }
    ws_h2       = @{ Mode = 'compare-ws-h2';       Count = 1 }
}

function Read-Map {
    if (-not (Test-Path -LiteralPath $MapPath)) { throw "Missing map: $MapPath" }
    return Get-Content -LiteralPath $MapPath -Raw | ConvertFrom-Json
}

function Save-Map($map) {
    ($map | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $MapPath -Encoding utf8
}

function Ensure-MacSlots($map) {
    if (-not $map.mac) {
        $map | Add-Member -NotePropertyName mac -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    foreach ($key in $MacShardPlans.Keys) {
        $n = $MacShardPlans[$key].Count
        $existing = @()
        if ($map.mac.PSObject.Properties.Name -contains $key) {
            $existing = @($map.mac.$key)
        }
        if ($ResetMacSlots -or $existing.Count -ne $n) {
            # New shard layout is not compatible with old 3-way Mac CSVs — reset.
            $keep = $null
            if (-not $ResetMacSlots -and $n -eq 1 -and $map.mac.PSObject.Properties.Name -contains $key) {
                $keep = @($map.mac.$key) | Where-Object { $_ -gt 0 } | Select-Object -First 1
            }
            $existing = @(1..$n | ForEach-Object { 0 })
            if ($keep) { $existing = @([long]$keep) }
        }
        $map.mac | Add-Member -NotePropertyName $key -NotePropertyValue @($existing) -Force
    }
    return $map
}

function Get-RunCsvCount([long] $RunId) {
    if ($RunId -le 0) { return 0 }
    try {
        $arts = Invoke-GhJson -GhArgs @('api', "repos/$Repo/actions/runs/$RunId/artifacts")
        $n = 0
        $incomplete = $false
        foreach ($a in @($arts.artifacts)) {
            if ($a.name -like 'rps-csv-macos*') { $n++ }
            if ($a.name -like 'rps-incomplete-macos*') { $incomplete = $true }
        }
        if ($incomplete) { return 0 }
        return $n
    }
    catch {
        Write-Log "artifact probe failed for $RunId : $($_.Exception.Message)"
        return -1
    }
}

function Get-RunStatus([long] $RunId) {
    if ($RunId -le 0) { return @{ status = 'none'; conclusion = $null } }
    $r = Invoke-GhJson -GhArgs @('api', "repos/$Repo/actions/runs/$RunId")
    return @{ status = $r.status; conclusion = $r.conclusion; created = $r.created_at }
}

function Find-NewRunId([datetime] $AfterUtc) {
    $list = Invoke-GhJson -GhArgs @(
        'api', "repos/$Repo/actions/workflows/rps-saturation.yml/runs?event=workflow_dispatch&per_page=15"
    )
    foreach ($r in @($list.workflow_runs)) {
        $created = [datetime]::Parse(
            $r.created_at.TrimEnd('Z'),
            $null,
            [System.Globalization.DateTimeStyles]::AssumeUniversal
        ).ToUniversalTime()
        if ($created -lt $AfterUtc.AddSeconds(-20)) { continue }
        return [long]$r.id
    }
    return 0
}

function Dispatch-Shard([string] $Mode, [string] $Shard) {
    $dispatchAt = (Get-Date).ToUniversalTime()
    if ($DryRun) {
        Write-Log "DRY-RUN would dispatch $Mode $Shard on $Ref"
        return 0
    }
    Write-Log "DISPATCH $Mode $Shard ref=$Ref"
    $null = Invoke-GhJson -AllowEmpty -GhArgs @(
        'workflow', 'run', 'rps-saturation.yml',
        '--repo', $Repo,
        '--ref', $Ref,
        '-f', "mode=$Mode",
        '-f', 'runner_os=macos-15',
        '-f', "arm_shard=$Shard",
        '-f', 'repeats=3'
    )
    Start-Sleep -Seconds 10
    for ($attempt = 1; $attempt -le 18; $attempt++) {
        $id = Find-NewRunId -AfterUtc $dispatchAt
        if ($id -gt 0) {
            Write-Log "DISPATCHED $Mode $Shard => $id"
            return $id
        }
        Start-Sleep -Seconds 5
    }
    throw "Could not resolve new run for $Mode $Shard after dispatch"
}

$map = Ensure-MacSlots (Read-Map)
Save-Map $map
Write-Log "Mac waiter start ref=$Ref map=$MapPath maxConcurrent=$MaxConcurrent"

while ($true) {
    $map = Ensure-MacSlots (Read-Map)
    $pending = [System.Collections.Generic.List[object]]::new()
    $inFlight = 0
    $ready = 0
    $total = 0
    $statusBits = [System.Collections.Generic.List[string]]::new()

    foreach ($key in $MacShardPlans.Keys) {
        $plan = $MacShardPlans[$key]
        $slots = @($map.mac.$key)
        for ($i = 0; $i -lt $plan.Count; $i++) {
            $total++
            $runId = [long]$slots[$i]
            $shard = "{0}/{1}" -f ($i + 1), $plan.Count

            if ($runId -le 0) {
                $pending.Add([pscustomobject]@{ Key = $key; Index = $i; Mode = $plan.Mode; Shard = $shard; RunId = 0; Why = 'not-dispatched' })
                $statusBits.Add("$key/$i=not-dispatched")
                continue
            }

            $st = Get-RunStatus $runId
            if ($st.status -eq 'in_progress' -or $st.status -eq 'queued') {
                $inFlight++
                $pending.Add([pscustomobject]@{ Key = $key; Index = $i; Mode = $plan.Mode; Shard = $shard; RunId = $runId; Why = 'in_progress' })
                $statusBits.Add("$key/$i=$runId/in_progress")
                continue
            }

            $csvN = Get-RunCsvCount $runId
            if ($csvN -gt 0) {
                $ready++
                continue
            }

            if ($csvN -lt 0) {
                # Transient API failure — do not redispatch yet.
                $inFlight++
                $statusBits.Add("$key/$i=$runId/api-error")
                continue
            }

            $pending.Add([pscustomobject]@{ Key = $key; Index = $i; Mode = $plan.Mode; Shard = $shard; RunId = $runId; Why = 'zero-csv-will-rd' })
            $statusBits.Add("$key/$i=$runId/zero-csv-will-rd")
        }
    }

    Write-Log "STATUS mac_ready=$ready/$total in_flight=$inFlight pending=$($statusBits -join ', ')"

    if ($ready -eq $total) {
        Write-Log "ALL MAC SHARDS READY"
        break
    }

    $slotsOpen = [Math]::Max(0, $MaxConcurrent - $inFlight)
    $toDispatch = @($pending | Where-Object { $_.Why -ne 'in_progress' } | Select-Object -First $slotsOpen)
    foreach ($p in $toDispatch) {
        try {
            $newId = Dispatch-Shard -Mode $p.Mode -Shard $p.Shard
            $arr = @($map.mac.($p.Key))
            while ($arr.Count -le $p.Index) { $arr += 0 }
            $arr[$p.Index] = $newId
            $map.mac | Add-Member -NotePropertyName $p.Key -NotePropertyValue @($arr) -Force
            Save-Map $map
        }
        catch {
            Write-Log "DISPATCH FAILED $($p.Mode) $($p.Shard): $($_.Exception.Message) — will retry"
        }
    }

    Start-Sleep -Seconds $PollSeconds
}

Write-Log "Mac waiter done. Next: download CSVs and run publish-rps-peers.ps1"
