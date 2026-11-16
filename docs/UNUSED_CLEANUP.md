# Reviewed unused declaration cleanup

The shared lexer recognizes case-insensitive `S:FS`, `S:N`, `S:Z`, `S:V`, `S:C`, `S:MINOR` and native `Device:I` / `Device:O` / `Device:C` roots, including numeric-slot forms such as `Local:1:I.Data[0]`. It preserves qualified roots and scans dynamic index expressions separately. These forms no longer produce an unsupported-qualified-address diagnostic or false plain-tag/type mentions. Recognition is lexical; it does not validate hardware, members or runtime behavior. [Rockwell status keywords](https://www.rockwellautomation.com/en-ua/docs/studio-5000-logix-designer/37-00/contents-ditamap/math-status-flags.html)

New previews expose additive `coverage`: `readableUnits`, `lexicalUnresolvedUnits`, `protectedAoiRoots`, `otherOpaqueRoots`, `unsupportedOrMissingUnits`, `readableLexicalCoverageComplete`, and `hiddenOrUnsupportedSourcePresent`. `partial` aggregates unresolved coverage and must not be interpreted as proof of protected content. A readable lexical problem retains matching candidate mentions with its actual diagnostic code. `protected-aoi-hidden-dependencies` is reserved for actual protected AOI `EncodedData`, including direct siblings of readable definitions under AddOnInstructionDefinitions. Three such siblings remain three protected roots even when all readable logic scans cleanly; their possible hidden dependencies stay retained.

New preview receipts also identify `producer` and `toolVersion`; the receipt SHA binds this metadata. Older receipts can lack coverage or producer fields. Missing historical metadata remains unknown and is not backfilled; completed applies retain their original evidence. Obtain a new reviewed preview if an uncompleted old review no longer matches the current producer.

`studio_unused_cleanup_preview` reviews an explicit set of whole tags, UDTs and AOI definitions in a registered saved controller export. It supports full exports within 64 MiB, 1000000 XML elements and 64 XML nesting levels. The export must declare `TargetType="Controller"` and `ContainsContext="false"`. It never infers complete references from a partial export or modifies the input.

Use the same standalone CLI workspace throughout. Discover each schema with `operation-help`. Example `arguments` for the preview:

```json
{
  "request": {
    "projectId": "<registered-id>",
    "expectedSha256": "<exact saved hash>",
    "expectedRevision": 0,
    "candidates": [
      {"kind": "tag", "name": "SpareRecord", "programName": "Example"},
      {"kind": "udt", "name": "SpareRecordType"},
      {"kind": "aoi", "name": "SpareInstruction"}
    ],
    "retain": [
      {
        "selection": {"kind": "tag", "name": "SpareRecord", "programName": "Example"},
        "category": "hmi",
        "reason": "Retained by the reviewed display binding inventory"
      }
    ]
  }
}
```

Omit `programName` for controller tags. Only tags accept a program. `retain` categories are `hmi`, `external` and `user`, with a nonempty reason. These decisions form part of the exact review hash. HMI projects and external client inventories are owner-reviewed inputs; this operation does not discover them or assert their absence.

The preview reports exact removable declarations and fingerprints, retained declarations with causes, and a bounded dependency graph. Candidates are a maximum scope: nothing outside them is removed automatically. Anything kept retains the selected declarations it depends on, transitively. RLL/ST operands, aliases and local shadowing, declared UDT/AOI types, visible AOI calls, MESSAGE/Produced/Consumed configuration and other visible configuration mentions participate. AOI parameter/local declarations shadow controller tags. Removing a closed group can remove its otherwise unused nested types or AOIs only when those definitions are also explicit candidates.

Tags with `ExternalAccess` missing or other than explicit `None` are retained. Produced/Consumed and other non-Base/Alias tag kinds, MESSAGE/I/O/system tags, safety/force/signature/protected declarations and context declarations are retained. Explicit HMI/external retains cannot be overridden by an empty lexical result. A kept alias, instance or definition retains its selected target/type dependencies. Signed or encoded AOIs are never deleted. Opaque or unsupported routines conservatively retain possible dependencies and report `partial`/diagnostics. Malformed lexical sources retain matching candidate mentions. Comments and ordinary quoted literals do not establish references. These saved-file checks do not prove compiler binding, execution or absence of external writers.

A surviving `EncodedData EncodedType="AddOnInstructionDefinition"` is an unresolved dependency root even if it exposes `Parameters` and has no encoded text. Its private LocalTags, nested types and AOI calls are unavailable. All selected possible dependencies remain retained, including StringFamily UDTs with no visible references. The preview reports `protected-aoi-hidden-dependencies` and `partial: true`; it does not claim complete closure from public parameter attributes. This applies both when the protected AOI is outside the candidate list and when it is selected but retained by protection rules. [ProtectedCleanupDemo.L5X](../examples/ProtectedCleanupDemo.L5X) models this parameters-only projection with four apparently unused StringFamily types and a spare AOI. It is synthetic saved-file evidence, not a native-import fixture or acceptance result.

The separate [structural workflow](../workflows/rockwell-engineering/references/structural-edits.md) also blocks UDT removal/layout changes when surviving protected AOIs can hide consumers. Its target-filtered removal scanner supports independently reviewed tag-only batches on full exports without a source-wide 100000-token ceiling, while retaining source-text and related-finding budgets, explicit scope/state dispositions and protected-byte checks.

Preview retains an immutable JSON `previewReceipt`. Review `removals`, `retentions`, `dependencies`, `partial`, `preservation` and `resultSha256`, then call `studio_unused_cleanup_apply` with:

```json
{
  "request": {
    "preview": "<complete unchanged preview request object>",
    "expectedReviewSha256": "<reviewSha256>",
    "previewReceiptId": "<previewReceipt.id>",
    "previewReceiptSha256": "<previewReceipt.sha256>"
  }
}
```

Replace the illustrative `preview` string with the original JSON request object. Apply rechecks the source revision/hash, receipt, graph and all preservation commitments. It creates a separate **REVIEW ONLY** managed project, exportable L5X candidate and JSON receipt. It preserves original encoding/BOM, annotations, saved native/value representations, addresses, configuration and every unselected byte. Exact retained declaration fingerprints are checked against the candidate readback. Save outputs to unused absolute paths with `studio_save_artifact`.

Repeated exact applies return the retained result. Inspect `studio_unused_cleanup_status` with `previewReceiptId` and `expectedSha256` equal to the preview receipt hash after interruption; indeterminate attempts are not replayed automatically. A review with no eligible removals cannot apply. The generic structural scope still excludes AOI edits; this dedicated workflow permits only reviewed whole-definition deletion.

The shared service exposes `POST /api/unused-cleanup/preview`, `POST /api/unused-cleanup/apply` and `GET /api/unused-cleanup/previews/{id}?expectedSha256=...`; the same operations are available through the extension operation editors. This workflow starts no SDK worker, controller connection, download or native verification. Native import/readback and deployment acceptance remain separate.

Budgets: 200 candidates and retention decisions, 100000 indexed declarations, 10000 selected dependency edges, 1 MiB per source unit/ST routine, 16 MiB total lexical source, 50 diagnostic examples and a 4 MiB review. Unrelated lexical tokens consume no dependency storage. Synthetic regressions cover closed groups, nested types, AOI/local scope, HMI/external/Produced retention, aliases, native MESSAGE references, unknown/protected content, receipt guards, exact-byte preservation and a full decorated controller with over 100000 unrelated operands.
