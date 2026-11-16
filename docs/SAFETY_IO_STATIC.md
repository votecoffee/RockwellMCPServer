# Static safety-I/O comparison

Read/compare saved module parameters through shared Core operations, the standalone CLI, MCP/API, or **Diagnostics → Safety I/O static comparison**. Leave native execution disabled. No SDK service, native AOP, controller, or source-file write is involved.

| Operation | Purpose |
| --- | --- |
| `studio_safety_io_static_profiles` | Discover supported installed identity/layout/enums and file/profile hashes. |
| `studio_safety_io_static_profile_inspect` | Inspect separately pinned identity/parameter XML. |
| `studio_safety_io_static_browse` | Decode an exact saved module with explicit encoding. |
| `studio_safety_io_static_compare` | Page channel differences and retain a JSON receipt. |

## Supported identities

| Catalog/series | Vendor/type/product/major | Configuration datatype | Layout bytes | Packed DWORDs / bytes |
| --- | --- | --- | ---: | ---: |
| `1734-IB8S/B` | `1 / 35 / 15 / 2` | `AB:1734_IB8S:C:0` | 90 | 23 / 92 |
| `1732ES-IB16/A` | `1 / 35 / 30 / 1` | `AB:1732ES_IB16:C:0` | 166 | 42 / 168 |
| `1732ES-IB8XOBV4/A` | `1 / 35 / 9 / 1` | `AB:1732ES_IB8XOB8:C:0` | 108 | 27 / 108 |

IB8XOBV4 uses the AOP's shared IB8XOB8 layout. Output fields retain AOP point/pair numbering, without an invented physical-terminal mapping. Other products/major revisions are unsupported. Catalog suffix series and matching separate `Series` are accepted; conflicting series, vendor/product or major are rejected. A base catalog without series is labeled as selected from the pinned identity.

Discovery reads installed RA 1734S Discrete / RA 1732ES Discrete XML without instantiating an AOP. Missing profiles are omitted. Caller-pinned copies remain labeled as unverified installed provenance. Physical order/types and bit-field mappings must match the adapter; offsets are not guessed.

## Decode and compare

Register preserved L5X sources. Discover exact schemas with `operation-help`. A `studio_safety_io_static_browse` body inside `arguments.request` is:

```json
{
  "projectId": "<saved-project-id>",
  "expectedSha256": "<project-sha256>",
  "expectedRevision": 0,
  "moduleName": "SafetyInput",
  "profile": {
    "profileId": "1734-IB8S/B",
    "files": {
      "identity": { "path": "<absolute-identity-xml>", "sha256": "<identity-sha256>" },
      "parameters": { "path": "<absolute-parameter-xml>", "sha256": "<parameter-sha256>" }
    }
  },
  "expectedProfileSha256": "<discovered-static-profile-sha256>",
  "encoding": "packed-dint32-le"
}
```

`packed-dint32-le` explicitly selects little-endian interpretation of signed decimal or unsigned decimal/radix DWORD literals. Count must match the table. Bytes outside the declared layout remain terminal padding evidence. `flat-bytes` requires exactly the declared number of signed/unsigned byte literals. Encoding is never auto-detected or converted.

Require one scalar `Data Format="L5K"` under one `ConfigData` or `ConfigTag`. Nested/typed aggregates, alternate/duplicate representations, malformed literals, incorrect counts and profile layout drift are refused.

The snapshot retains source/module/configuration hashes, decoded payload hash, encoding/counts, original `ConfigSize`, `CfgSize`/`CfgIDNum`, signature words, reserved bits and padding. `moduleContextTextSha256` hashes exact UTF-8 module text after removing only the selected Data element, exposing saved IP/SNN/timing/context differences separately. Complete source/module hashes retain the original binding.

Native envelope and size-word semantics are **not validated**. A supplied 1734 export can retain `ConfigSize="82"`, leading `CfgSize=86`, 23 DWORDs, and a 90-byte layout plus two terminal bytes. These are separate evidence. The reader never rewrites them to agree or asserts vendor acceptance. Arithmetic decoding does not establish another model's native envelope/SCID contract.

Call `studio_safety_io_static_compare` with complete `before` and `after` browse requests plus optional `offset`, `limit`, `includeUnchanged`. Explicit module names may differ; profile ID/hash must match. Cross-family/channel alignment is not inferred.

- `parametersEqual`, `changedParameters` and paged `rows` cover channel fields only. Resume `nextOffset` with identical pinned queries; `comparisonSha256` stays stable across pages.
- Inspect `hasUnknownParameterValues`. Equal unknown numeric codes do not establish semantic or safety equivalence.
- `nonParameterDifferences` retains signature/envelope/reserved/internal-padding differences. Inspect `moduleContextEqual`, `nativeConfigSizeEqual` and `terminalPaddingEqual` separately.
- Export the immutable JSON `receipt` using `studio_save_artifact` to a new absolute path. Retain its SHA-256. It includes both snapshots, producer/version, exact page and `nativeAcceptance:"not-performed"`.

Channel fields include input modes/test sources, delays/discrepancy/latch settings, test-output modes and supported output modes/pairs. No device matching, auto-alignment, write proposal or safety approval is generated.

## Preservation and limits

Saved XML, L5K formatting, native payload bytes, signature tuples and padding remain unchanged. These readers add no packed/1732 editing or signature generation. Native configuration/import/save/readback and functional safety acceptance remain separately authorized owner work. [Native SCID boundary](SAFETY_MODULE_CONFIGURATION.md)

Saved XML uses shared 64-MiB / 1000000-element / 64-level limits. Each AOP file is at most 1 MiB / 20000 elements / 32 levels; DTD/external resolution is prohibited. Vectors are at most 16 KiB with exact counts. Pages allow 1..200 rows and offset 0..4096.

Generated fixtures cover all layouts, packed/flat/radix words, reported 1734 envelope, nonzero signatures/padding, strict identities, unchanged sources, channel/context differences, unknown enums, paging, stale pins and malformed/out-of-range input. Installed AOP XML inspection is separate from native AOP/controller acceptance, which has not been performed.
