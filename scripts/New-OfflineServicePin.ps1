#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppRoot, [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedServiceVersion,
    [string]$PredecessorStateRoot, [string]$ResourceRegistry, [switch]$ReviewedLegacyUnboundContract)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'StableMcp.psm1') -Force -DisableNameChecking
$AppRoot = Assert-RockwellLauncherPath $AppRoot; $Destination = Assert-RockwellLauncherPath $Destination
if (Test-Path -LiteralPath $Destination) { throw 'Pin output already exists; preserve it and prepare a fresh candidate.' }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $AppRoot 'artifacts/app/Service/Studio5000.Service.dll')).ProductVersion
if ($version -cne $ExpectedServiceVersion) { throw 'The accepted service product version differs from the reviewed version.' }
$entries = @()
foreach ($relative in @('artifacts/app/Service','artifacts/app/ExtensionWorker')) {
    $directory = Assert-RockwellLauncherPath (Join-Path $AppRoot $relative)
    foreach ($item in @(Get-ChildItem -LiteralPath $directory -Recurse -Force)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Offline runtime links are unsupported.' }
        if (!$item.PSIsContainer) {
            $entries += [ordered]@{ path=$item.FullName.Substring($AppRoot.Length+1).Replace('\','/'); size=$item.Length; sha256=(Get-RockwellLauncherHash $item.FullName) }
        }
    }
}
$script = Assert-RockwellLauncherPath (Join-Path $AppRoot 'scripts/Start-Service.ps1')
$entries += [ordered]@{ path='scripts/Start-Service.ps1'; size=([IO.FileInfo]::new($script).Length); sha256=(Get-RockwellLauncherHash $script) }
$previous = @()
$reviewed = @()
$unreviewed = @()
$pointerRevision = $null
if ($ResourceRegistry -and !$PredecessorStateRoot) { throw 'Predecessor review requires the explicit stable state root and physical registry together.' }
if ($PredecessorStateRoot) {
    if (!$ResourceRegistry -and !$ReviewedLegacyUnboundContract) { throw 'Use the saved route physical registry with -ResourceRegistry for predecessor review.' }
    $state = Assert-RockwellLauncherPath $PredecessorStateRoot
    if ($Destination.StartsWith($state + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Prepare the pin outside the immutable launcher state.' }
    $pointer = Join-Path $state 'launcher/current.json'
    $pointerRevision = Get-RockwellLauncherHash $pointer
    $selected = Get-RockwellStableSelection $state
    if ($selected.appRoot -ine $AppRoot) { throw 'Predecessor selection belongs to another application root.' }
    $current = [ordered]@{ directory=$selected.snapshot; files=@($selected.manifest.files);
        sourceCommit=$selected.manifest.source_commit; backendManifestSha256=$selected.selection.backend_manifest_sha256 }
    if ($ReviewedLegacyUnboundContract) {
        if ($selected.manifest.source_commit -cne '31a57922bde082f82567ebcc035b29ab0a35d9da') { throw 'Only the explicitly reviewed legacy source has this compatibility contract.' }
        $current['unboundLaunchContract']='windows-private-job-bind-before-resume-v1'
    }
    $previous += $current
    if ($ResourceRegistry) {
        $registry = Assert-RockwellLauncherPath $ResourceRegistry
        if ($Destination.StartsWith($registry + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Prepare the pin outside the resource registry.' }
        $inspector = Assert-RockwellLauncherPath (Join-Path ([IO.Path]::GetDirectoryName($Destination)) 'Studio5000.StdioHost.exe')
        $observation = & $inspector --inspect-frontdoor-images $registry
        if ($LASTEXITCODE -ne 0) { throw 'Typed predecessor inspection failed.' }
        $observation = ($observation | Out-String) | ConvertFrom-Json
        if ($null -eq $observation.images -or @($observation.images).Count -gt 4096) { throw 'Invalid bounded predecessor inspection.' }
        $backendRoot = Join-Path $state 'launcher/backends'
        $wanted = @{}
        foreach ($image in @($observation.images)) {
            $imageDirectory = [IO.Path]::GetDirectoryName($image)
            $prefix = [IO.Path]::GetFileName($imageDirectory)
            if ([IO.Path]::GetFileName($image) -in @('Studio5000.StdioHost.exe','Studio5000.Mcp.exe') -and
                $prefix -cmatch '^[a-f0-9]{32}$' -and [IO.Path]::GetDirectoryName($imageDirectory) -ieq $backendRoot -and
                $imageDirectory -ine $selected.snapshot) { $wanted[$prefix] = $true }
        }
        if ($wanted.Count -gt 7) { throw 'Unresolved metadata predecessors exceed the eight-closure pin budget.' }
        $found = @{}
        if ($wanted.Count -gt 0) {
            $history = Assert-RockwellLauncherPath (Join-Path $state 'launcher/history')
            if (Test-Path -LiteralPath $history -PathType Container) {
                $receipts = @(Get-ChildItem -LiteralPath $history -File -Filter 'selection-*.json' | Select-Object -First 1025)
                if ($receipts.Count -gt 1024) { throw 'Predecessor publication receipt inventory exceeds 1024.' }
                foreach ($receipt in $receipts | Sort-Object Name) {
                    if ($receipt.Name -cnotmatch '^selection-([a-f0-9]{64})\.json$') { continue }
                    $receiptSha = $Matches[1]
                    try {
                        $past = Read-RockwellLauncherJson $receipt.FullName 65536
                        if ($past.backend_id -cnotmatch '^[a-f0-9]{64}$') { continue }
                        $prefix = $past.backend_id.Substring(0,32)
                        if (!$wanted.ContainsKey($prefix) -or $found.ContainsKey($prefix)) { continue }
                        if ($past.schema_version -ne 1 -or $past.contract -cne 'rockwell-owned-stdio-v1' -or $past.app_root -ine $selected.appRoot -or
                            $past.backend_manifest_sha256 -cnotmatch '^[a-f0-9]{64}$' -or $past.launcher_sha256 -cnotmatch '^[a-f0-9]{64}$' -or
                            $past.module_sha256 -cnotmatch '^[a-f0-9]{64}$' -or $past.selection_sha256 -cne (Get-RockwellSelectionIdentity $past) -or
                            (Get-RockwellLauncherHash $receipt.FullName) -cne $receiptSha) { continue }
                        $snapshot = Get-RockwellBackendDirectory $state $past.backend_id
                        $manifestPath = Join-Path $snapshot 'backend.json'
                        $manifest = Read-RockwellLauncherJson $manifestPath
                        if ((Get-RockwellLauncherHash $manifestPath) -cne $past.backend_manifest_sha256 -or
                            'frontdoor.json' -cnotin @($manifest.files.path) -or (Assert-RockwellBackend $snapshot $manifest) -cne $past.backend_id) { continue }
                        $found[$prefix] = [ordered]@{ directory=$snapshot; files=@($manifest.files);
                            sourceCommit=$manifest.source_commit; backendManifestSha256=$past.backend_manifest_sha256 }
                        $reviewed += [ordered]@{ backendId=$past.backend_id; publicationReceiptSha256=$receiptSha }
                    } catch { continue } # Missing or damaged receipts cannot authorize an image.
                }
            }
        }
        foreach ($prefix in $wanted.Keys | Sort-Object) {
            if ($found.ContainsKey($prefix)) { $previous += $found[$prefix] }
            else { $unreviewed += $prefix }
        }
    }
    $inherited = Join-Path $selected.snapshot 'offline-service.json'
    if (Test-Path -LiteralPath $inherited) {
        $old = Read-RockwellLauncherJson $inherited
        foreach ($entry in @($old.previousFrontends)) {
            # Re-derive metadata paths from current typed observations. Keep the
            # separately reviewed execution compatibility pins within the budget.
            if ($ResourceRegistry -and !$entry.unboundLaunchContract -and 'frontdoor.json' -cin @($entry.files.path)) { continue }
            $existing = @($previous | Where-Object { $_.directory -ieq $entry.directory })
            if ($existing.Count -ne 0) {
                if ($entry.unboundLaunchContract) {
                    if ($existing[0].sourceCommit -cne $entry.sourceCommit -or $existing[0].backendManifestSha256 -cne $entry.backendManifestSha256) {
                        throw 'Inherited legacy provenance differs from its publication.'
                    }
                    $existing[0]['unboundLaunchContract'] = $entry.unboundLaunchContract
                }
                continue
            }
            if ($previous.Count -ge 8) {
                if ($entry.unboundLaunchContract) { throw 'The pin budget cannot preserve the separately reviewed legacy contract.' }
                continue
            }
            $copy = [ordered]@{ directory=(Assert-RockwellLauncherPath $entry.directory); files=@($entry.files) }
            if ($entry.sourceCommit -or $entry.backendManifestSha256) {
                $copy['sourceCommit']=$entry.sourceCommit; $copy['backendManifestSha256']=$entry.backendManifestSha256
            }
            if ($entry.unboundLaunchContract) { $copy['unboundLaunchContract']=$entry.unboundLaunchContract }
            $previous += $copy
        }
    }
}
elseif ($ReviewedLegacyUnboundContract) { throw 'An explicit reviewed predecessor selection is required.' }
$totalBytes = [long]0
foreach ($entry in $entries) { $totalBytes += [long]$entry['size'] }
if ($entries.Count -gt 256 -or $totalBytes -gt 268435456) { throw 'Offline pin inventory exceeds its bound.' }
$value = [ordered]@{ schemaVersion=1; serviceVersion=$ExpectedServiceVersion; files=$entries; previousFrontends=$previous;
    predecessorPointerSha256=$pointerRevision }
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($value | ConvertTo-Json -Depth 20 -Compress) + [Environment]::NewLine)
if ($bytes.Length -gt 262144) { throw 'Offline pin metadata exceeds its bound.' }
if ($pointerRevision -and (Get-RockwellLauncherHash $pointer) -cne $pointerRevision) { throw 'Predecessor selection changed during review.' }
$stream = [IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
[ordered]@{ path=$Destination; sha256=(Get-RockwellLauncherHash $Destination); files=$entries.Count; previousFrontends=$previous.Count; serviceVersion=$ExpectedServiceVersion; backendExecuted=[bool]$ResourceRegistry; protocolStarted=$false;
    predecessorPointerSha256=$pointerRevision; reviewedPredecessors=$reviewed; unreviewedSnapshotPrefixes=$unreviewed } | ConvertTo-Json -Depth 6
