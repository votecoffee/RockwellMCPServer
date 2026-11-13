# On-demand Rockwell engineering

Rockwell Engineering CLI 2.3.0 runs with the .NET 8 runtime on Windows. It starts an owned worker for each engineering request and calls the same project handlers used by the desktop service. Publish from this checkout with the .NET 8 SDK.

~~~powershell
$deploy = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RockwellEngineering\middleware\stage-2.3.0'
.\scripts\Publish-EngineeringMiddleware.ps1 -Destination $deploy
$cli = Join-Path $deploy 'cli\Rockwell.EngineeringCli.exe'
$l5x = (Resolve-Path -LiteralPath '.\examples\Demo.L5X').Path
& $cli --help
& $cli l5x-summary --input $l5x --timeout-seconds 30
~~~

Choose a new publish destination for each build. Add `-IncludeVendorBridges` to build the local SDK and CIP bridges with the SDK package supplied by the licensed Rockwell installation. Publication builds files without opening a vendor application. The CLI needs no Rockwell desktop service. The repository-root `Run.cmd` still opens the separate shared-service GUI.

## Job contract

An operation takes an absolute input path or request path. Job stdout is a single JSON object with a version, operation, exit code, result or error, and cleanup status. Stderr carries the diagnostic. The L5X summary includes the input size and SHA-256 of the bytes parsed. Counts describe the supplied document, including partial exports. Admitted jobs keep an intent and result under the user's local application data in RockwellEngineering\jobs.

Independent jobs use distinct working directories and receipts. Help, operations, operation-help and sdk-catalog start no workers. Windows assigns owned children to a private job during suspended creation. The CLI checks process cleanup before returning. A deadline or cancellation may be followed by a brief cleanup interval. File apply uses exclusive publication of a new output. A native hold or interrupted step may require review even when process cleanup is confirmed.

| Exit code | Meaning |
| --- | --- |
| 0 | Operation completed |
| 1 | Worker or input operation failed |
| 2 | Invalid CLI arguments |
| 69 | Native pilot disabled or blocked, or unsupported platform |
| 70 | Middleware or cleanup verification failed |
| 124 | Job deadline exceeded |
| 130 | Job cancelled |

## Discover and run engineering operations

Use `operations` to enumerate names and `operation-help <name>` for the typed argument schema. The catalog lists named operations and direct engineering routes. These are two ways to address overlapping functionality. Direct names include the verb and route template, such as `GET /api/projects/{id}/elements`. Pass route/query fields as arguments and a POST body as the `request` argument when the schema lists it.

~~~powershell
& $cli operations
& $cli operation-help studio_register_project
& $cli operation-help run
& $cli run --request 'C:\Reviews\register.json'
~~~

~~~json
{
  "workspace": "example",
  "operation": "studio_register_project",
  "arguments": { "request": { "path": "C:\\Projects\\Example.L5X" } }
}
~~~

The result returns a managed project ID and source/current hashes. Keep the same workspace for later operations that use its IDs and preview, transaction, artifact or comparison receipts. Use new absolute output paths and the operation's exact expected revision/hash fields. The managed copy and the saved export are separate. Each workspace lives under `%LOCALAPPDATA%\RockwellEngineering\workspaces`. Commands, including reads, take an exclusive lease for that workspace. If a command returns `workspace_busy`, wait for the owning invocation to finish before submitting another request in the same workspace. Separate workspace names can run independently.

Use a `steps` array in place of `operation` and `arguments` for a bounded sequence. Each step accepts an operation, arguments and optional `waitMilliseconds` after dispatch. Use known IDs in a batch. Complete durable plans and references from earlier commands across turns. There are up to 24 steps, 120 seconds of wait per step, 240 seconds of total wait and a command deadline of up to 300 seconds. An error stops the sequence. Use the transaction operations for supported atomic offline stages. Each completed step has a receipt under the job's `steps` directory, including when a later step fails. Large values are kept in the receipt file. An intent without a result can indicate interrupted work.

### Native and controller access

The default workspace run can inspect and edit saved exports. Set `allowNative: true` on a reviewed request to allow the locally published bridges. For ACD work, use the licensed Logix Designer SDK Service, the matching Logix Designer major version and a disposable input or managed artifact. SDK 2.1.974 is the pinned bridge dependency. SDK job calls and version conversion wait for their terminal job result in the command. Read the durable job evidence and use a fresh export or native reopen when the workflow calls for readback.

For controller operations, first configure the intended target in `%LOCALAPPDATA%\RockwellEngineering\workspaces\<workspace>\settings.json`. The settings fields are `enableControllerOperations`, `controllerTargets`, `enableSdkServiceRecovery`, optional `allowedRoots` and `outputRoots` arrays of absolute paths, and optional `factoryTalkSchemaDirectory`. Each target specifies `id`, `communicationsPath` and an optional operation/program/routine `policy` and `cipRead` configuration. Use the exact target ID at the top level of the run request and in any operation arguments that require it. Keep the target and permitted methods scoped to the authorized task. Target settings are private machine data. [SDK target configuration](SDK.md) · [Watch configuration](WATCHES.md)

To poll, create or bind a stopped watch first. Run `studio_watch_start` in a native-enabled batch, wait for a bounded interval, and query the watch, recording or trigger results in later steps. The command stops its watches before exiting and writes stop receipts. Configure recording if samples must be available after the batch. Start the installed SDK service manually or use `studio_sdk_service_ensure_running` with an explicit authorized request ID and workspace recovery enabled. The command can start a stopped service and observe startup. The service is Windows-owned and startup is never automatic in this CLI.

Native dispatch retains a workspace marker and, for an explicitly configured online target, a communications-path marker. The parent clears its markers after verified worker cleanup and recorded native close. If either receipt is missing, preserve the original state and inspect the linked job and affected target before allowing new native work. Windows process cleanup does not establish controller effects.

For a same-snapshot native L5K and GM `derived_input`, use the [bound L5K workflow](BOUND_L5K.md). Plan, review and record are saved-file operations. Only the reviewed SDK job needs native permission.

## Review and publish a saved L5X copy

Save request objects as UTF-8 JSON without a byte-order mark and pass their absolute paths using --request.

1. Run l5x-inspect with sourcePath and kind. Use tags with an optional program, routines with a program, or rungs with a program and routine. The result returns the source hash, exact XPaths, scalar saved tag values where present, and editableTexts. Offset and limit control pages of up to 100 rows.
2. Run l5x-preview with sourcePath, expectedSha256, xpath, and text. Use an exact inspected XPath. This creates preview.L5X and a review.json with the original and proposed text, source hash, and proposed output hash. Inspect the review before publishing.
3. Run l5x-apply with reviewPath, reviewSha256, and a new absolute outputPath. This rechecks the review and source bytes, recomputes the change, publishes the new file, and returns its hash and readback text. The apply-intent file in the job directory identifies any temporary output to inspect if a response is lost.

This workflow changes an existing scalar tag or routine description or rung comment. It preserves unselected bytes using the Core scalar editor. Localized entries, ladder logic, and structural edits use different workflows.

## Offline SDK pilot

The earlier dedicated pilot commands remain available. The sdk-catalog command reads the frozen SDK surface and lists methods permitted by the local-project profile. To use these commands, place a reviewed native-pilot.json beside the CLI with an absolute workspaceRoot and the bridgeSha256 for that workspace's artifacts\app\NativeBridge\Studio5000.NativeBridge.exe. Use a dedicated workspace and the existing native-local candidate procedure for SDK dependencies. [Native-local candidate preparation](NATIVE_LOCAL_PILOT.md)

The typed sdk-read-export command opens a disposable saved ACD snapshot, reads one scalar tag in Offline mode, and returns a detailed L5X artifact through the existing SDK job engine. The operation accepts sourcePath, expectedSha256, tag, dataType, optional program, and optional exportName. Use sdk-read-export-plan with the same request to verify the input fingerprint and inspect the three calls without starting the SDK. A plan does not validate the native ACD format.

~~~json
{
  "sourcePath": "C:\\Pilot\\fixtures\\Disposable.ACD",
  "expectedSha256": "<64 hex digits>",
  "program": "NativeLocalProbe",
  "tag": "PilotInput",
  "dataType": "DINT",
  "exportName": "review.L5X"
}
~~~

The generic sdk-offline command takes a JSON SDK run request via --request. The existing job engine accepts the local-project method allowlist, checks source and import fingerprints, and remaps SDK writes to working copies and generated artifacts. For a processor catalog query, the request has no source.

~~~json
{"calls":[{"method":"GetProcessorTypesAsync","arguments":{"majorRev":35}}]}
~~~

The pilot disables controller targets, SDK-service autostart, and native GUI launch. If a native dispatch lacks verified close, the local native-pending.json links to the retained job and blocks further native work. Review the exact job and native session before clearing the marker. The CLI never retries a dispatch.

## Parity with historical Rockwell workflows

| Area | Standalone CLI and next handoff |
| --- | --- |
| Offline Logix XML and documentation | Tag/routine/rung and UDT browsing, where-used, multilingual documentation, structural previews/applies, packaging, transactions, validation and saved exports. [API and file editing](API.md) |
| ACD and SDK | Native open, read, write, import, build, export, version upgrade and readback use the existing SDK job policy. Native acceptance requires disposable version-matched inputs and the licensed vendor runtime. [Native pilot](NATIVE_LOCAL_PILOT.md) |
| Project compare and data | Ladder compare and drawings, saved comparison reviews, tag-value and logic merge, UDT extraction, logic tracing, captured-value rung evaluation, watch polling, trends and triggered recording. Polling needs a bounded batch. Online capture requires an authorized target. [Project review](PROJECT_REVIEW.md) · [Value merge](VALUE_MERGE.md) · [Watches](WATCHES.md) |
| FactoryTalk View and View Designer | FactoryTalk ME/SE XML, references, language files and View Designer export folders and languages. FactoryTalk graphics edits require matching installed schemas. Operators open the vendor editors for native import, compile, runtime checks and deployment. [FactoryTalk](FACTORYTALK.md) · [View Designer](VIEW_DESIGNER.md) · [HMI languages](HMI_LANGUAGES.md) |
| Online Logix | Existing SDK paths cover configured online access and accepted or finalized import. Standalone Test Edits, Untest Edits and Assemble are operator actions in Studio. The vendor runtime and controller must be checked during an authorized trial. [Online edits](ONLINE_EDITS.md) |

## Optional MCP adapter

Launch the separately published mcp-adapter\Rockwell.EngineeringMcp.exe with --cli followed by the absolute Rockwell.EngineeringCli.exe path. The adapter exposes rockwell_l5x_summary. Connection and discovery use static metadata. A tool call starts the same gated CLI and returns its JSON result. A client cancellation closes the CLI's cancellation pipe. Registering this optional adapter is a separate host action.

The chat skill source is available for a host with local Windows execution. [Rockwell engineering CLI skill](../workflows/rockwell-engineering-cli/SKILL.md)
