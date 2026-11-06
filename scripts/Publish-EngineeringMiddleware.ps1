#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination, [switch]$IncludeMcpAdapter)
$ErrorActionPreference = 'Stop'
if ($Destination -notmatch '^[A-Za-z]:[\\/]') { throw 'Use an absolute local Windows destination.' }
$Destination = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) { throw 'Choose a new destination to preserve any existing deployment.' }
$repo = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $Destination 'cli'
$adapter = Join-Path $Destination 'mcp-adapter'
Push-Location -LiteralPath $repo
try {
    dotnet publish 'src/Studio5000.EngineeringCli/Studio5000.EngineeringCli.csproj' -c Release -p:RestoreLockedMode=true -o $cli --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Rockwell CLI publish failed.' }
    if ($IncludeMcpAdapter) {
        dotnet publish 'src/Studio5000.EngineeringMcp/Studio5000.EngineeringMcp.csproj' -c Release -p:RestoreLockedMode=true -o $adapter --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Optional Rockwell MCP adapter publish failed.' }
    }
    [pscustomobject]@{
        cli = Join-Path $cli 'Rockwell.EngineeringCli.exe'
        adapter = if ($IncludeMcpAdapter) { Join-Path $adapter 'Rockwell.EngineeringMcp.exe' }
    } | ConvertTo-Json
} finally { Pop-Location }
