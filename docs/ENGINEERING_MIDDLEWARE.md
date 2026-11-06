# On-demand Rockwell engineering

The Windows CLI runs from a local folder with the .NET 8 runtime. It uses the existing Rockwell Core parser and starts an owned worker for an actual file job. Publish from this checkout with the .NET 8 SDK.

~~~powershell
$deploy = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RockwellEngineering\middleware'
.\scripts\Publish-EngineeringMiddleware.ps1 -Destination $deploy -IncludeMcpAdapter
$cli = Join-Path $deploy 'cli\Rockwell.EngineeringCli.exe'
$l5x = (Resolve-Path -LiteralPath '.\examples\Demo.L5X').Path
& $cli --help
& $cli l5x-summary --input $l5x --timeout-seconds 30
~~~

Choose a new publish destination for an additional deployment. The optional switch creates a separate adapter directory. Publication only builds files. It registers neither an app service nor an MCP connection.

## Job contract

An operation takes an absolute input path or request path. Job stdout is a single JSON object with a version, operation, exit code, result or error, and cleanup status. Stderr carries the diagnostic. The L5X summary includes the input size and SHA-256 of the bytes parsed. Counts describe the supplied document, including partial exports. Admitted jobs keep an intent and result under the user's local application data in RockwellEngineering\jobs.

All four middleware apps share the Windows desktop-session mutex Local\OpenAIEngineeringMiddleware.Job.v1. Job admission is a zero-timeout wait with no queue. Help, operations, and sdk-catalog are static. Windows assigns owned children to a private job during suspended creation. The CLI checks cleanup before returning. A deadline or cancellation may therefore be followed by a brief cleanup interval.

| Exit code | Meaning |
| --- | --- |
| 0 | Operation completed |
| 1 | Worker or input operation failed |
| 2 | Invalid CLI arguments |
| 69 | Native pilot disabled or blocked, or unsupported platform |
| 70 | Middleware or cleanup verification failed |
| 75 | Another engineering job holds the session mutex |
| 124 | Job deadline exceeded |
| 130 | Job cancelled |

## Offline SDK pilot

The first package has no SDK bridge or native-pilot.json. The sdk-catalog command reads the existing frozen SDK surface and lists methods permitted by the local-project profile. A future reviewed deployment can place native-pilot.json beside the CLI with an absolute workspaceRoot and the bridgeSha256 for that workspace's artifacts\app\NativeBridge\Studio5000.NativeBridge.exe. Use a dedicated workspace and the existing native-local candidate procedure for SDK dependencies. [Native-local candidate preparation](NATIVE_LOCAL_PILOT.md)

The sdk-offline command takes a JSON SDK run request via --request. The existing job engine accepts the local-project method allowlist, checks source and import fingerprints, and remaps SDK writes to working copies and generated artifacts. For a processor catalog query, the request has no source.

~~~json
{"calls":[{"method":"GetProcessorTypesAsync","arguments":{"majorRev":35}}]}
~~~

The pilot disables controller targets, SDK-service autostart, and native GUI launch. If a native dispatch lacks verified close, the local native-pending.json links to the retained job and blocks further native work. Review the exact job and native session before clearing the marker. The CLI never retries a dispatch.

## Optional MCP adapter

Launch the separately published mcp-adapter\Rockwell.EngineeringMcp.exe with --cli followed by the absolute Rockwell.EngineeringCli.exe path. The adapter exposes rockwell_l5x_summary. Connection and discovery use static metadata. A tool call starts the same gated CLI and returns its JSON result. A client cancellation closes the CLI's cancellation pipe. Registering this optional adapter is a separate host action.

The chat skill source is available for a host with local Windows execution. [Rockwell engineering CLI skill](../workflows/rockwell-engineering-cli/SKILL.md)
