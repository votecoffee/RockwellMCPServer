# Source-bound project cloning and retargeting

Use `studio_project_clone_preview`, `studio_project_clone_apply` and
`studio_project_clone_readback` for a separate offline clone of a pinned, saved,
whole-controller L5X snapshot. Discover each operation's current schema first.
These operations live in Core/shared service; the Logix extension and GUI call
the same implementation. They never start a native job or connect to a PLC.

## Review the contract

Start with the current saved project, including its saved values. If reviewed
logic must be combined with fresher values, complete the separate
`preserve-fresh` logic/value merge first and select that result as the clone
source. A clone preserves values from its exact source; it does not fetch live
values or silently mix a second project into the clone.

The caller supplies a reviewed equipment/address contract. GM tooling owns GM
workbook role interpretation. This engine does not infer equipment roles,
discover every compiler-bound symbol reference, identify all runtime writers,
or replace every matching address in the file.

`ProjectCloneRequest` contains:

- `source`: managed `projectId` and exact `sha256`.
- `name`: the new managed project's display name. File naming and controller
  identity are separate operations.
- `identities`: keyed `before`/`after` identifiers with explicit `sites`.
- `addresses`: keyed canonical dotted IPv4 `before`/`after` mappings, explicit
  `sites`, and a bounded `runtimeReview` coverage statement.

Every site has a `purpose`, a `field`, and optional `valueGroup`. Each field has
an absolute element `xPath`, optional attribute name, and `valueSha256`:
SHA-256 of the UTF-8 **decoded complete XML value**, without a BOM. Hash the
parsed attribute/scalar value, not its serialized XML or its source byte span.
Source/project hashes still identify the original complete bytes. Predicates
must select one existing element; descendant axes, functions and unions are
excluded. Unsupported/ambiguous sites produce blocked findings.

All offsets refer to the original decoded source. Changes are applied
simultaneously. An old peer IP may equal a new self IP without causing a second
replacement. Overlapping sites block apply instead of selecting an order.
Identifier spelling is exact; include every reviewed declaration/reference
spelling and scope. For reference discovery, use the separate controller-tag
rename workflow before assembling a clone contract.

## Site purposes and saved encodings

| Purpose | Supported location |
| --- | --- |
| `controller-name` | `Controller/@Name` |
| `project-target-name` | `RSLogix5000Content/@TargetName` |
| `symbol-declaration` | Standard Tag, Program, Routine, Module or Task `@Name` |
| `symbol-reference` | Exact RLL/ST scalar text, or supported AliasFor/MainRoutineName/ProgramName/RoutineName attributes |
| `controller-comm-path` | `Controller/@CommPath` |
| `module-address` | Exported `Module/Ports/Port/@Address` |
| `message-path` | `MessageParameters/@CommPath` |
| `saved-value` | Supported tag `Data` scalar representations |
| `hidden-cache` | Explicit reviewed AOI instance L5K path |
| `runtime-initializer` | Exact RLL/ST address text or numeric literal |

The controller name and whole-project TargetName must agree after the edit.
Declaration collisions block apply. Explicit symbol sites do not establish
compiler binding, alias capture safety or complete external/HMI coverage.

| Field `codec` | Contract and behavior |
| --- | --- |
| `text` | Exact whole identifier/IP occurrence at `offset`. Saved tag data uses typed codecs, except MESSAGE CommPath. |
| `integer` | Exact scalar Value attribute or scalar at an explicit `l5kPath`. Choose `octet` 0..3, or a packed 32-bit address with `byteOrder` `network` or `little-endian`. Pin `integerBits` 8/16/32 and `signed`; exported Decorated type must agree. Numeric RLL/ST initializer literals also require `offset` and `tokenLength`. |
| `string` | Saved quoted Logix byte literal. Supply original `capacity`, `padded`, and a pinned `lengthField`. The latter must be the same String Data `@Length` or the matching Decorated STRING `LEN` sibling Value. |
| `l5k-string` | Exact `l5kPath` selecting `[LEN, 'byte literal']`. Original byte-buffer capacity and padding are checked; both LEN and bytes are updated. |
| `cip-string` / `cip-l5k-string` | As above, with `linkOffset` selecting the start of a reviewed binary CIP port segment with an extended ASCII link address. Validates the old length/address/pad, updates the ASCII-address length byte, adds/removes the segment's alignment pad, then repairs the outer saved LEN and buffer padding. Extended port-number encodings and other binary layouts are unsupported. |

L5K paths are explicit zero-based nested list indexes; `[]` selects the root.
No UDT/AOI memory layout is guessed from the Decorated member order. Integers
are decoded without floating-point conversion, including radix bit patterns,
and selected integers are emitted in decimal with the reviewed signedness.
Selected byte literals may be re-escaped as `$HH`; their decoded untouched
prefix/suffix bytes remain equal. Zero padding retains the original reviewed
capacity. Nonzero bytes beyond LEN and insufficient capacity block apply.

For octet fields, set the same nonempty `valueGroup` on the corresponding L5K
and Decorated sites. Every changed octet must be selected in each representation
of that group. Separate multiple logical IP members in one tag into separate
groups. Every exported Data representation of a changed tag needs coherent
sites; unknown/raw/opaque representations block that change and are retained.
Unselected tags, including unknown formats, remain byte exact.

A hidden-only AOI cache can have no Decorated counterpart. Supply
`hiddenLocalName` and a reason in `hiddenLocalReason`, plus its exact L5K path.
The named exported AOI LocalTag must exist. This validates the exception's
identity, not a compiler-derived association between that local and an index;
the reviewed equipment contract owns that association. Raw formats still block.

List runtime initializer sites under the **same address mapping** as their saved
caches. Every listed site must decode to `before` and is rewritten to `after`.
Use `runtimeReview` to record the checked initialization paths and any absence
of applicable writers. Unlisted initializers, indirect writers, SSV behavior and
protected code are not analyzed. This is explicit coverage, not a runtime proof.

## Preview, apply and readback

1. Register/select the current source and inspect exact XML fields. Populate the
   equipment contract, decoded-value hashes, representation groups and runtime
   coverage. A mapping supports 1..4096 total sites; contracts support up to 256
   identity/address keys, 64 MiB input/output, 1 MiB per selected field and an
   8 MiB review report.
2. Run `studio_project_clone_preview` (`POST /api/project-clone/preview`). Review
   fields, blocked findings, caller coverage statements and `reviewSha256`.
   Preview changes no project.
3. Run `studio_project_clone_apply` (`POST /api/project-clone/apply`) with
   `{ "preview": <same contract>, "expectedReviewSha256": <review hash> }`.
   Sources and the complete review are recomputed. A stale source/hash/contract,
   collision or blocked finding prevents publication. Apply creates a new
   managed project and immutable JSON receipt. It leaves the source unchanged.
4. Export a separate file through the ordinary export/artifact workflow. Native
   import/compile and any authorized readback are separate actions. No automatic
   controller action or retry is part of cloning.
5. Register the supplied saved readback, then run `studio_project_clone_readback`
   (`POST /api/project-clone/readback`) with the same preview/review hash and a
   pinned `readback` snapshot. The expectation is rebuilt from the original
   source. Compare the whole saved XML, including saved values and protected
   configuration. Differences include field locations, bounded to 256 details.

Readback normalizes attribute order, container whitespace and CDATA/text
spelling; it ignores root ExportDate and Controller LastModifiedDate. It keeps
rung numbers, data values, descriptions, signatures and configuration
significant. The document element is compared; the XML declaration, prolog and
epilog are not. Strict matches may have different raw byte hashes. For additional
native serialization differences, use the separate integrity/native-readback
validation workflow with explicit reviewed normalizations. Supplied files never
authenticate native origin: `nativeOriginVerified` remains false.

In the GUI, open **Diagnostics & workflows > Clone and retarget**. Source and
Candidate selections seed the contract/readback selectors. The operation editor
accepts the reviewed JSON contract and preview hash. There is no role-guessing
wizard; the same operations are also available under **Extensions > Logix**.

## Separate scopes and acceptance

Controller CommPath is the saved engineering communication route; a module's
Port Address is saved project routing configuration. Neither operation programs
a physical device's TCP/IP interface. Physical IP settings require an appropriate
separately authorized native/device workflow. The [Rockwell import/export manual](https://literature.rockwellautomation.com/idc/groups/literature/documents/rm/1756-rm014_-en-p.pdf)
documents these exported controller/module/tag structures.

Safety logic/configuration, SNNs, signatures, ownership and AOI/type definitions
are excluded from clone edits. They remain present and unchanged; preservation
does not establish their validity for a new installation. Style/recipe edits
belong in a separately scoped operation. No SNN is generated, and an XML check,
receipt or saved-file match never establishes native or functional acceptance.
