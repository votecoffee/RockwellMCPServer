# Reviewed unused declaration cleanup

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
