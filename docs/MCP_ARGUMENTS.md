# Generic MCP extension arguments

Discover an operation through `studio_extensions_catalog`, then read its exact input schema and effect with `studio_extensions_describe`. Pin that generation on the call. The `call.arguments` value accepts either an object or a string containing exactly one JSON object:

```json
{
  "call": {
    "extensionId": "logix",
    "operation": "studio_capabilities",
    "generation": "COPY_FROM_THE_CURRENT_CATALOG",
    "arguments": "{}"
  }
}
```

The pinned .NET MCP SDK emits an unconstrained schema for `JsonElement`. A registered client's generated signature can project this field to `string`; sending the advertised serialized object must therefore work. The adapter decodes that representation once before HTTP forwarding. Existing direct-object calls remain compatible. The same normalization applies to Read, Preview, Apply, Control and the nested `request.call` in `studio_extensions_start_job`.

Objects retain JSON numeric/text values without conversion through floating point. Malformed JSON, arrays, scalars, double-encoded strings, undefined values, excessive nesting and oversized inputs are rejected locally as `invalid_arguments`. Both the incoming representation and decoded object are bounded to the existing 1 MiB character limit; parsing uses the existing extension JSON depth budget. The read-only service contract remains object-only. Schema, generation, effect/channel, source policy and authorization checks still run in the shared service. There is no retry, catalog refresh, channel promotion or job replay.

Run focused checks with:

```powershell
dotnet test tests/Studio5000.Mcp.Tests/Studio5000.Mcp.Tests.csproj
```

These tests use the pinned SDK's schema generator and argument binder, the production error wrapper, and a temporary loopback fixture backed by an in-process echo worker. They reproduce the old string-forwarding rejection and check all generic channels, nested job payload capture, direct objects, invalid values, size/depth limits, stale generations, schema/effect rejection and one-request behavior. Job requests are captured without dispatch; no installed service, selected MCP route, native application, engineering project or controller is used.

Source/tests and a staged frontend package do not update an existing installation or chat. A changed actual canary and normal publication require their own reviewed package/selection step. Extension reload alone does not replace this fixed MCP adapter.
