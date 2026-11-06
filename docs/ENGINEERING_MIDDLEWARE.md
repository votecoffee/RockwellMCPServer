# On-demand Rockwell engineering

The Windows CLI runs from a local folder with the .NET 8 runtime. It uses the existing Rockwell Core parser and starts an owned worker for an actual file job. Publish from this checkout with the .NET 8 SDK.

~~~powershell
$deploy = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RockwellEngineering\middleware'
.\scripts\Publish-EngineeringMiddleware.ps1 -Destination $deploy
$cli = Join-Path $deploy 'cli\Rockwell.EngineeringCli.exe'
$l5x = (Resolve-Path -LiteralPath '.\examples\Demo.L5X').Path
& $cli --help
& $cli l5x-summary --input $l5x --timeout-seconds 30
~~~

Choose a new publish destination for an additional deployment. Publication only builds files. The CLI needs no app service or MCP connection.

## Job contract

An operation takes an absolute input path or request path. Job stdout is a single JSON object with a version, operation, exit code, result or error, and cleanup status. Stderr carries the diagnostic. The L5X summary includes the input size and SHA-256 of the bytes parsed. Counts describe the supplied document, including partial exports. Admitted jobs keep an intent and result under the user's local application data in RockwellEngineering\jobs.

Independent file jobs use distinct working directories and receipts. Help, operations, and sdk-catalog are static. Windows assigns owned children to a private job during suspended creation. The CLI checks cleanup before returning. A deadline or cancellation may be followed by a brief cleanup interval. File apply uses exclusive publication of a new output, and SDK work retains its dedicated workspace lock and native recovery marker.

| Exit code | Meaning |
| --- | --- |
| 0 | Operation completed |
| 1 | Worker or input operation failed |
| 2 | Invalid CLI arguments |
| 69 | Native pilot disabled or blocked, or unsupported platform |
| 70 | Middleware or cleanup verification failed |
| 124 | Job deadline exceeded |
| 130 | Job cancelled |

## Review and publish a saved L5X copy

Save request objects as UTF-8 JSON without a byte-order mark and pass their absolute paths using --request.

1. Run l5x-inspect with sourcePath and kind. Use tags with an optional program, routines with a program, or rungs with a program and routine. The result returns the source hash, exact XPaths, scalar saved tag values where present, and editableTexts. Offset and limit control pages of up to 100 rows.
2. Run l5x-preview with sourcePath, expectedSha256, xpath, and text. Use an exact inspected XPath. This creates preview.L5X and a review.json with the original and proposed text, source hash, and proposed output hash. Inspect the review before publishing.
3. Run l5x-apply with reviewPath, reviewSha256, and a new absolute outputPath. This rechecks the review and source bytes, recomputes the change, publishes the new file, and returns its hash and readback text. The apply-intent file in the job directory identifies any temporary output to inspect if a response is lost.

This workflow changes an existing scalar tag or routine description or rung comment. It preserves unselected bytes using the Core scalar editor. Localized entries, ladder logic, and structural edits use different workflows.

## Offline SDK pilot

The first package has no SDK bridge or native-pilot.json. The sdk-catalog command reads the existing frozen SDK surface and lists methods permitted by the local-project profile. A future reviewed deployment can place native-pilot.json beside the CLI with an absolute workspaceRoot and the bridgeSha256 for that workspace's artifacts\app\NativeBridge\Studio5000.NativeBridge.exe. Use a dedicated workspace and the existing native-local candidate procedure for SDK dependencies. [Native-local candidate preparation](NATIVE_LOCAL_PILOT.md)

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
| Offline Logix XML and documentation | Summary, scoped tag/routine/rung inspection, and scalar description/comment preview, apply, and readback are ready. Structural edits, multilingual annotations, routine/program packaging, and preservation reviews still need CLI entry points over existing Core logic. [API and file editing](API.md) |
| ACD and SDK | The typed open/read/export and generic offline job routes are staged. Run native acceptance serially on a disposable, version-matched project with the licensed SDK and verified owner close. [Native pilot](NATIVE_LOCAL_PILOT.md) |
| Project compare and data | Ladder compare, saved tag value/logic merge, watch/UDT reads, trends and triggered recording have existing engines that still need standalone commands. Online capture requires an authorized target. [Project review](PROJECT_REVIEW.md) · [Value merge](VALUE_MERGE.md) · [Watches](WATCHES.md) |
| FactoryTalk View and View Designer | ME/SE XML and language files and View Designer export folders have existing file workflows to expose. Native HMI import, compile, runtime checks, and deployment need the vendor tools and operator verification. [FactoryTalk](FACTORYTALK.md) · [View Designer](VIEW_DESIGNER.md) · [HMI languages](HMI_LANGUAGES.md) |
| Online Logix | Existing SDK paths cover authorized online access and accepted or finalized import. Standalone Test Edits, Untest Edits, and Assemble need Studio. Functional verification and mode-dependent finalization remain separate from a file or SDK result. [Online edits](ONLINE_EDITS.md) |

## Optional MCP adapter

Launch the separately published mcp-adapter\Rockwell.EngineeringMcp.exe with --cli followed by the absolute Rockwell.EngineeringCli.exe path. The adapter exposes rockwell_l5x_summary. Connection and discovery use static metadata. A tool call starts the same gated CLI and returns its JSON result. A client cancellation closes the CLI's cancellation pipe. Registering this optional adapter is a separate host action.

The chat skill source is available for a host with local Windows execution. [Rockwell engineering CLI skill](../workflows/rockwell-engineering-cli/SKILL.md)
