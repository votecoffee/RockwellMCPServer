param([switch]$ReviewBlockedStop, [ValidatePattern('^[a-f0-9]{32}$')][string]$ObserveRequestId,
    [ValidateRange(1,300)][int]$WaitSeconds = 20)
$ErrorActionPreference = 'Stop'
function Read-RockwellStopJson([string]$Path) {
    # Pollers must share delete as the owner atomically replaces its receipt.
    for ($attempt=0; ; $attempt++) {
        $stream=$null
        try {
            $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $bytes=New-Object byte[] 32769; $count=0
            while($count -lt $bytes.Length) { $read=$stream.Read($bytes,$count,$bytes.Length-$count);if(!$read){break};$count+=$read }
            if($count -gt 32768){throw 'Stop metadata exceeds its bounded read limit.'}
            return ([Text.Encoding]::UTF8.GetString($bytes,0,$count).TrimStart([char]0xfeff) | ConvertFrom-Json)
        } catch {
            $failure=$_.Exception;while($failure.InnerException){$failure=$failure.InnerException}
            if($failure -isnot [IO.IOException] -or ($failure.HResult -band 0xffff) -notin @(2,32,33) -or $attempt -ge 6){throw}
            Start-Sleep -Milliseconds (25 -shl $attempt)
        } finally { if($stream){$stream.Dispose()} }
    }
}
function Publish-RockwellStopIntent([string]$Temporary, [string]$Target, [scriptblock]$ReplaceFile) {
    # Retry only unchanged-name metadata replacement. No new request is minted
    # and no app/resource close operation is repeated by this helper.
    for ($attempt=0; ; $attempt++) {
        try {
            if (Test-Path -LiteralPath $Target) {
                if ($ReplaceFile) { & $ReplaceFile $Temporary $Target }
                else { [IO.File]::Replace($Temporary,$Target,[NullString]::Value) }
            } else { [IO.File]::Move($Temporary,$Target) }
            return
        } catch {
            $failure = $_.Exception
            while ($failure.InnerException) { $failure = $failure.InnerException }
            if ($failure -isnot [IO.IOException] -or ($failure.HResult -band 0xffff) -notin @(32,33,1175) -or
                $attempt -ge 6 -or !(Test-Path -LiteralPath $Temporary) -or !(Test-Path -LiteralPath $Target)) { throw }
            Start-Sleep -Milliseconds (25 -shl $attempt)
        }
    }
}
function Get-RockwellDirectStopSignature([string]$Key, [string]$OwnerId, [string]$RequestId) {
    $body = @('rockwell-stop-v1', $OwnerId, $OwnerId, $RequestId) -join [char]10
    $hmac = New-Object Security.Cryptography.HMACSHA256
    try {
        $hmac.Key = [Text.Encoding]::UTF8.GetBytes($Key)
        return -join ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($body)) | ForEach-Object { $_.ToString('x2') })
    } finally { $hmac.Dispose() }
}
function Read-RockwellDirectStopReply([string]$Path, [string]$OwnerId, [string]$RequestId) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $reply = Read-RockwellStopJson $Path
        if ($reply.schemaVersion -eq 1 -and $reply.ownerId -ceq $OwnerId -and $reply.requestId -ceq $RequestId) { return $reply }
    }
}
if ($ObserveRequestId -and $ReviewBlockedStop) { throw 'Observation cannot request another stop review.' }
$repo = Split-Path -Parent $PSScriptRoot
$pidFile = Join-Path $repo '.local/service.pid'
$serviceProcessId = if (Test-Path -LiteralPath $pidFile) { [int](Get-Content -LiteralPath $pidFile -Raw).Trim() }
elseif (!$ObserveRequestId) { throw 'No background service PID file was found.' }
$supervisorFile = Join-Path $repo '.local/management-supervisor.json'
if (Test-Path -LiteralPath $supervisorFile) {
    $owned = Read-RockwellStopJson $supervisorFile
    if ($owned.schemaVersion -eq 2) {
        if ($ObserveRequestId -or $PSBoundParameters.ContainsKey('WaitSeconds')) { throw 'These observation options require a direct service owner.' }
        if ($owned.servicePid -ne $serviceProcessId -or $owned.ownerId -notmatch '^[0-9a-f]{32}$' -or
            $owned.resourceId -notmatch '^[0-9a-f]{32}$' -or !$owned.resourceRegistry) { throw 'Managed ownership receipt differs; no stop was requested.' }
        $resourcePath = Join-Path ([IO.Path]::GetFullPath($owned.resourceRegistry)) ('resources/' + $owned.resourceId + '.json')
        $resource = Read-RockwellStopJson $resourcePath
        if ($resource.schema_version -ne 1 -or $resource.app_id -ne 'rockwell-mcp-server' -or
            $resource.resource_id -ne $owned.resourceId -or $resource.owner_id -ne $owned.ownerId -or
            $resource.ownership -ne 'private' -or $resource.containment -ne 'windows_job' -or
            $resource.process.pid -ne $serviceProcessId -or $resource.state -ne 'active' -or
            ($resource.recovery_barrier -and $resource.recovery_barrier -cne 'Service shutdown requires generation-bound native close evidence.') -or $resource.receipt) { throw 'Managed resource is not an active unclaimed private tree; inspect its retained record.' }
        $ownedProcess = Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue
        if (!$ownedProcess) { throw 'Recorded service is gone; inspect the retained resource/cleanup outcome instead of replaying stop.' }
        try {
            [void]$ownedProcess.Handle # Pin before checking creation identity.
            $expectedImage = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/app/Service/Studio5000.Service.exe'))
            if ($ownedProcess.HasExited -or !$ownedProcess.Path -or
                !([IO.Path]::GetFullPath($ownedProcess.Path).Equals($expectedImage, [StringComparison]::OrdinalIgnoreCase)) -or
                !([IO.Path]::GetFullPath($resource.process.executable).Equals($expectedImage, [StringComparison]::OrdinalIgnoreCase)) -or
                $ownedProcess.StartTime.ToUniversalTime().ToFileTimeUtc().ToString([Globalization.CultureInfo]::InvariantCulture) -cne [string]$resource.process.created) {
                throw 'Managed process identity changed; no stop was requested.'
            }
            $key = [IO.File]::ReadAllText((Join-Path $repo '.local/service.key')).TrimStart([char]0xfeff).Trim()
            if ($key.Length -lt 32 -or $key.Length -gt 4096) { throw 'Private stop authentication is unavailable.' }
            $requestPath = Join-Path $repo '.local/service-stop-request.json'
            $requestId = [Guid]::NewGuid().ToString('N')
            if (Test-Path -LiteralPath $requestPath) {
                $prior = Read-RockwellStopJson $requestPath
                if ($prior.ownerId -eq $owned.ownerId -and $prior.resourceId -eq $owned.resourceId) {
                    if ($prior.requestId -notmatch '^[0-9a-f]{32}$') { throw 'Existing stop request is invalid; preserve it for inspection.' }
                    $requestId = $prior.requestId # Observe the same intent, never replace its ID.
                    $priorReplyPath = Join-Path $repo '.local/service-stop-result.json'
                    if (Test-Path -LiteralPath $priorReplyPath) {
                        $priorReply = Read-RockwellStopJson $priorReplyPath
                        if ($priorReply.ownerId -eq $owned.ownerId -and $priorReply.resourceId -eq $owned.resourceId -and $priorReply.requestId -eq $requestId) {
                            if ($priorReply.state -eq 'blocked' -and $ReviewBlockedStop) { $requestId = [Guid]::NewGuid().ToString('N') }
                            elseif ($priorReply.state -in @('blocked','uncertain','completed')) { throw ('Prior owner stop '+$priorReply.state+'; inspect it. -ReviewBlockedStop applies only after resolving a verified zero-effect refusal.') }
                        }
                    }
                }
            }
            $body = "rockwell-stop-v1`n$($owned.ownerId)`n$($owned.resourceId)`n$requestId"
            $hmac = New-Object System.Security.Cryptography.HMACSHA256
            try { $hmac.Key = [Text.Encoding]::UTF8.GetBytes($key); $signature = -join ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($body)) | ForEach-Object { $_.ToString('x2') }) }
            finally { $hmac.Dispose() }
            $request = [ordered]@{ schemaVersion = 1; ownerId = $owned.ownerId; resourceId = $owned.resourceId; requestId = $requestId; signature = $signature }
            $temporary = $requestPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
            try {
                [IO.File]::WriteAllText($temporary, ($request | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding($false)))
                Publish-RockwellStopIntent $temporary $requestPath
            } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
            $waitDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
            while (!$ownedProcess.WaitForExit(100)) {
                $replyPath = Join-Path $repo '.local/service-stop-result.json'
                if (Test-Path -LiteralPath $replyPath) {
                    $reply = Read-RockwellStopJson $replyPath
                    if ($reply.ownerId -eq $owned.ownerId -and $reply.resourceId -eq $owned.resourceId -and $reply.requestId -eq $requestId -and $reply.state -in @('blocked','uncertain')) {
                        throw ('Owner stop '+$reply.state+': '+$reply.message+' Inspect the retained request/resource; no PID was stopped.')
                    }
                }
                if ([DateTimeOffset]::UtcNow -ge $waitDeadline) { throw 'Owner stop has no confirmed terminal outcome; inspect the same request/resource and do not retry with another ID.' }
            }
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
            do {
                $result = Read-RockwellStopJson $resourcePath
                if ($result.receipt -and $result.receipt.outcome -in @('terminated','already_exited')) { Write-Output 'Managed owner confirmed resource-tree cleanup.'; return }
                if ($result.receipt -and $result.receipt.outcome -eq 'uncertain') { throw 'Resource cleanup is uncertain; preserve the receipt and do not replay.' }
                Start-Sleep -Milliseconds 100
            } while ([DateTimeOffset]::UtcNow -lt $deadline)
            throw 'Service exited without a verified cleanup receipt; inspect retained ownership evidence.'
        } finally { $ownedProcess.Dispose() }
    }
}
$ownerPath = Join-Path $repo '.local/service-owner.json'
if (!(Test-Path -LiteralPath $ownerPath)) { throw 'No creation-bound service ownership receipt exists. Do not terminate from this legacy PID file; inspect the old instance manually.' }
$direct = Read-RockwellStopJson $ownerPath
if ($direct.schemaVersion -ne 1 -or $direct.ownerId -cnotmatch '^[0-9a-f]{32}$' -or $direct.owner.pid -le 0 -or
    ($null -ne $serviceProcessId -and $direct.owner.pid -ne $serviceProcessId) -or $direct.owner.created -notmatch '^[0-9]{1,24}$') {
    throw 'Direct service ownership generation differs; no stop requested.'
}
$serviceProcessId = $direct.owner.pid
$expected = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/app/Service/Studio5000.Service.exe'))
if (![IO.Path]::GetFullPath($direct.owner.executable).Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Direct service ownership image differs; no stop requested.'
}
$baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
try { $cryptoKey = $baseKey.OpenSubKey('SOFTWARE\Microsoft\Cryptography'); try { $machine = ([Guid]::Parse([string]$cryptoKey.GetValue('MachineGuid'))).ToString('D').ToLowerInvariant() } finally { if ($cryptoKey) { $cryptoKey.Dispose() } } } finally { $baseKey.Dispose() }
$sha = [Security.Cryptography.SHA256]::Create()
try { $hostId = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($machine)) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
if ($direct.owner.host_id -cne $hostId) { throw 'Foreign/unverifiable service ownership host; no local PID lookup is permitted.' }
$requestPath = Join-Path $repo '.local/service-owner-stop.json'
$replyPath = Join-Path $repo '.local/service-owner-stop-result.json'
if ($ObserveRequestId) {
    $prior = if (Test-Path -LiteralPath $requestPath) { Read-RockwellStopJson $requestPath }
    if ($prior.ownerId -cne $direct.ownerId -or $prior.requestId -cne $ObserveRequestId) {
        throw 'The retained stop request does not match the requested ID and exact owner.'
    }
}
$process = $null
try {
    try { $process = Get-Process -Id $serviceProcessId -ErrorAction Stop }
    catch { if ($_.FullyQualifiedErrorId -notlike 'NoProcessFoundForGivenId,*') { throw } }
    if ($process) {
        [void]$process.Handle
        if (!$process.Path -or !([IO.Path]::GetFullPath($process.Path).Equals($expected, [StringComparison]::OrdinalIgnoreCase)) -or
            $process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString([Globalization.CultureInfo]::InvariantCulture) -cne [string]$direct.owner.created) {
            throw 'Direct service process identity changed; no stop requested.'
        }
    }

    $publishLock = $null
    try {
        if (!$ObserveRequestId -and $process -and !$process.HasExited) {
            $publishLock = [IO.File]::Open((Join-Path $repo '.local/service-owner-stop-publish.lock'),
                [IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        }
        if (!$ObserveRequestId) { $prior = if (Test-Path -LiteralPath $requestPath) { Read-RockwellStopJson $requestPath } }
        $sameOwner = $prior -and $prior.ownerId -ceq $direct.ownerId
        if (!$sameOwner -and !$publishLock) { throw 'No retained stop request for the exact service owner.' }
        $key = [IO.File]::ReadAllText((Join-Path $repo '.local/service.key')).TrimStart([char]0xfeff).Trim()
        if ($key.Length -lt 32 -or $key.Length -gt 4096) { throw 'Private stop authentication is unavailable.' }
        $priorReply = $null
        if ($sameOwner) {
            $requestId = $prior.requestId
            if ($prior.schemaVersion -ne 1 -or $prior.resourceId -cne $direct.ownerId -or $requestId -cnotmatch '^[0-9a-f]{32}$' -or
                $prior.signature -cne (Get-RockwellDirectStopSignature $key $direct.ownerId $requestId)) {
                throw 'Retained stop request authentication differs; preserve it for inspection.'
            }
            $priorReply = Read-RockwellDirectStopReply $replyPath $direct.ownerId $requestId
        }
        if ($ReviewBlockedStop -and $priorReply.state -ne 'blocked') { throw 'A new review requires the matching verified blocked receipt.' }
        if (!$sameOwner -or $ReviewBlockedStop) {
            if (!$publishLock -or $process.HasExited) { throw 'The exact service owner is gone; no stop was requested.' }
            $requestId = [Guid]::NewGuid().ToString('N')
            $value = [ordered]@{ schemaVersion=1; ownerId=$direct.ownerId; resourceId=$direct.ownerId; requestId=$requestId;
                signature=(Get-RockwellDirectStopSignature $key $direct.ownerId $requestId) }
            $temporary = $requestPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
            try {
                [IO.File]::WriteAllText($temporary, ($value | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding($false)))
                if ($prior) {
                    $before = Read-RockwellStopJson $requestPath
                    if ($before.ownerId -cne $prior.ownerId -or $before.requestId -cne $prior.requestId -or $before.signature -cne $prior.signature) {
                        throw 'The retained stop request changed during review.'
                    }
                    Publish-RockwellStopIntent $temporary $requestPath
                } else { [IO.File]::Move($temporary,$requestPath) }
            } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
        } elseif (!$ObserveRequestId -and $priorReply.state -in @('blocked','uncertain')) {
            throw ('Prior stop '+$priorReply.state+'; inspect its outcome. Use -ReviewBlockedStop only after a verified zero-effect refusal is resolved.')
        }
    } finally { if ($publishLock) { $publishLock.Dispose() } }

    $wait = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $exited = !$process -or $process.WaitForExit(100)
        $reply = Read-RockwellDirectStopReply $replyPath $direct.ownerId $requestId
        if ($reply.state -in @('blocked','uncertain')) { throw ('Direct service stop '+$reply.state+': '+$reply.message) }
        if ($exited) {
            $exitPath = Join-Path $repo '.local/service-owner-exit.json'
            $closed = if (Test-Path -LiteralPath $exitPath) { Read-RockwellStopJson $exitPath }
            if ($reply.state -ne 'completed' -or $closed.schemaVersion -ne 1 -or $closed.state -cne 'closed' -or $closed.ownerId -cne $direct.ownerId -or
                $closed.owner.host_id -cne $direct.owner.host_id -or $closed.owner.pid -ne $direct.owner.pid -or
                $closed.owner.created -cne $direct.owner.created -or $closed.owner.executable -ine $direct.owner.executable) {
                throw 'Service exit lacks matching verified shutdown evidence.'
            }
            Write-Output "Direct service owner confirmed cleanup and shutdown. Request $requestId, owner $($direct.ownerId)."
            return
        }
        if ($wait.Elapsed.TotalSeconds -ge $WaitSeconds) {
            $state = switch ($reply.state) {
                'reviewing' { 'owner reviewing' }
                'completed' { 'waiting for the original process to exit' }
                default { 'awaiting owner receipt' }
            }
            Write-Output "Direct service stop is still pending ($state). Request $requestId, owner $($direct.ownerId), PID $serviceProcessId, created $($direct.owner.created)."
            Write-Output "Resume observation with .\scripts\Stop-Service.ps1 -ObserveRequestId $requestId."
            exit 2
        }
    }
} finally { if ($process) { $process.Dispose() } }
