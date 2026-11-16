# Reviewed native protected-AOI dependency evidence

CLI 2.8.0 adds a narrowly scoped route for deleting explicitly reviewed **readable AOI definitions** while opaque AOIs survive in a full saved controller export. It uses retained native `PartialExportToXmlFileAsync` dependency context. It never decrypts source, infers private closure from public Parameters, or provides a force/acknowledge-unknown switch. Customer engineering, native jobs and acceptance remain owner-authorized work.

## Prepare native evidence

Use one CLI workspace. Discover the installed schemas with `operation-help` for:

- `studio_protected_dependency_export_plan` (preview)
- `studio_sdk_start_job` (control; separate explicit offline native authorization)
- `studio_protected_dependency_preview` (preview)
- `studio_protected_dependency_record` (apply; metadata/artifacts only)
- `studio_unused_cleanup_preview`, `studio_unused_cleanup_apply`, `studio_unused_cleanup_status`

For full controller files, give saved-file reviews an explicit bounded deadline such as `run --request C:\Reviews\review.json --timeout-seconds 120`. Native runs use their separately authorized bounded deadline, normally 300 seconds. A timeout is not an instruction to replay a native job.

Register the exact full saved L5X with `studio_register_project`. Prepare the export plan with this `arguments` object:

```json
{
  "request": {
    "source": {"projectId": "<source-id>", "sha256": "<exact-source-hash>"},
    "aoiNames": ["ProtectedRootA", "ProtectedRootB"]
  }
}
```

The plan freezes an immutable copy and returns `sdkRequest`. It opens that exact copy and requests one partial AOI export per root using the SDK's logical path `Controller/AddOnInstructionDefinitions/AddOnInstructionDefinition[@Name='…']`. The installed SDK has no dependency-options argument on this method; the emitted files must actually contain `Dependencies` and `ForceProtectedEncoding` export options. An absent flag or context is a blocked review, never an inferred default.

Under explicit offline native authorization, submit the returned `sdkRequest` unchanged through `studio_sdk_start_job` with `allowNative: true`, no controller target, and the appropriate bounded timeout. Use the matching installed Logix engine. Retain the exact SDK job ID and output artifact IDs/hashes. Do not retry uncertain jobs automatically. Plan/review/record/cleanup themselves dispatch no native work.

For existing retained jobs, the selected source must be either:

- The exact SHA-256 of the private SDK input L5X.
- The exact full native L5X readback artifact from the **same job** that opened a saved ACD and exported the dependencies. An ACD hash or an unrelated L5X is not an equivalent source pointer.

Jobs must have succeeded, closed through the ordinary SDK lifecycle, contain only open/partial-export/save methods, and have no controller target/effects, error diagnostics or failed steps. SDK assembly identity is required. New generic SDK jobs record it; legacy jobs without it remain insufficient evidence. Retain them and obtain owner authorization before any replacement job.

## Review and issue the receipt

Call `studio_protected_dependency_preview` with:

```json
{
  "request": {
    "source": {"projectId": "<source-id>", "sha256": "<exact-source-hash>"},
    "exports": [
      {"aoiName": "ProtectedRootA", "jobId": "<sdk-job-id>", "artifactId": "<native-partial-export-id>", "artifactSha256": "<native-export-hash>"}
    ]
  }
}
```

List every root whose hidden dependency guard must be resolved. Every export must target its exact named opaque AOI, have matching major revision and complete context, and originate in the retained job. Manifested UDT layouts and readable AOI definitions must match the selected source; top-level export `Use` decoration is excluded from these contracts. Visible dependent types and calls must have concrete native context. Missing or materially normalized definitions block issuance.

CLI 2.8.1 also recognizes explicit native built-in context that a full `No ProductDefinedTypes` export can omit. A known name from the maintained `logix-builtins-v1` registry, `Class="ProductDefined"` and matching family metadata are required (`StringFamily` for STRING, `NoFamily` for the other known types). These entries appear separately in each root's `intrinsicTypes`, with registry, class/family, native layout and exact element hashes. BOOL/DINT/SINT/INT/REAL primitive entries and structured STRING/TIMER entries are covered. Their native metadata is retained for review; no source definition/hash or canonical layout is invented. Every explicit source definition must still match, including a source UDT with a built-in-looking name. Unknown product-defined names, absent/wrong class/family, missing user UDTs and changed user layouts receive no exemption. Receipt reuse also rejects conflicting new definitions. All native type rows count against the existing bounds.

After updating from 2.8.0, re-preview the same retained successful SDK job and artifacts, then review/record the new hash. This saved-file correction needs no new SDK dispatch. Previously issued receipts with no intrinsic rows retain their original hashes and can still be revalidated.

From CLI 2.8.2, new source snapshots, dependency receipts and cleanup artifacts describe their own workflow in `validation`. They no longer inherit the generic structural component-batch description. Existing immutable artifacts retain their original text and hashes; this metadata correction does not change their evidence or imply additional native acceptance.

`ForceProtectedEncoding` can produce different ciphertext. The receipt records original protected-element hashes and native exported-element hashes **separately**; it never compares these as equivalent payloads. Native dependency authority comes from the exact pinned input/job/export relationship, not metadata similarity. Readable source becoming opaque in an export is insufficient for a readable-definition contract.

Review `canIssue`, every dependency, finding and provenance hash. Issue the receipt with `studio_protected_dependency_record` using the complete unchanged preview request in `preview` and the exact `reviewSha256` in `expectedReviewSha256`. This rechecks all evidence and returns `receipt` and `sourceSnapshot` artifacts. Both are exportable with `studio_save_artifact`; cleanup consumes the retained workspace receipt ID/hash.

## Review named readable AOI removals

Add the receipt to a normal cleanup request:

```json
{
  "request": {
    "projectId": "<review-candidate-id>",
    "expectedSha256": "<exact-candidate-hash>",
    "expectedRevision": 0,
    "candidates": [
      {"kind": "aoi", "name": "ReviewedSpareA"},
      {"kind": "aoi", "name": "ReviewedSpareB"}
    ],
    "nativeDependencyEvidence": [
      {"artifactId": "<issued-receipt-id>", "sha256": "<exact-receipt-hash>"}
    ]
  }
}
```

When this field is supplied, **every candidate must be a readable AOI**. Tags, UDTs and protected AOIs cannot be removed through this route. Every AOI listed in a native dependency manifest stays retained, as do dependencies reached from other retained readable logic/declarations/configuration. Uncovered protected roots, other opaque content and unsupported source keep their existing conservative protection. The initial consumer supports direct sibling `EncodedData EncodedType="AddOnInstructionDefinition"` roots under `AddOnInstructionDefinitions`; other opaque representations remain protected.

The receipt can be used on another saved variant only when its original protected bytes and every manifested source type/AOI contract match. Controller identity, addresses, routine edits or saved tag values outside those library contracts may differ. Changed original ciphertext, changed/missing manifested types/AOIs, duplicate root evidence, stale jobs or tampered artifacts are refused. Preview and first apply revalidate the issued receipt, frozen original source and retained native evidence; they do not depend on the original managed project's current revision remaining unchanged.

Coverage reports `nativeDependencyRootsCovered` and `protectedAoiRootsUncovered` alongside the total `protectedAoiRoots`. `partial` and `hiddenOrUnsupportedSourcePresent` remain true while opaque source survives: private tag/external/runtime bindings have not been proved. The narrow reviewed library closure permits eligible readable-AOI removals without claiming those other forms of completeness.

Review the exact cleanup preview/receipt, then use the existing reviewed apply/status workflow. It creates a separate **REVIEW ONLY** candidate and receipt. All original protected bytes, every tag/value representation, UDT, module/address configuration and all other unselected bytes remain unchanged. Original input files stay unchanged. Native import/Build/readback and deployment acceptance remain separate owner work; a static cleanup result does not establish them.

For separately authorized native candidate acceptance, the existing `studio_sdk_start_job` contract supports `sourceArtifactId: "<cleanup-candidate-id>"`, `expectedSha256: "<candidate-hash>"` and these calls:

```json
[
  {"method": "OpenLogixProjectAsync", "arguments": {}},
  {"method": "BuildAsync", "arguments": {}},
  {"method": "SaveAsAsync", "arguments": {"savePath": "reviewed-readback.l5x", "detailedL5x": true}}
]
```

Use `allowNative: true`, the matching installed engine and no controller target. Retain the new native job ID, full diagnostics and readback hash. Inspect native readback with the existing `native-readback` validation profile against the intended candidate, retaining reported normalization/opaque differences. Build success does not establish deployment/runtime acceptance or exported ciphertext identity. This is a separate acceptance job, not part of evidence issuance and not a retry of a prior uncertain job.

## Bounds and verification

Evidence is limited to 16 roots per review, 16 receipts per cleanup, 64 MiB per native XML / 128 MiB total, 2048 manifested definitions per root / 8192 total, 200 findings, 2 MiB native job metadata and an 8 MiB complete evidence report. Cleanup retains its full-controller XML, lexical, graph and response limits. No truncated proof is issued.

Synthetic regressions exercise three opaque roots with deliberately re-encoded exports, manifested hidden types/AOI retention, exact two-AOI removal with byte preservation, incomplete-root fallback, stale/mismatched native evidence, original-ciphertext and contract drift, ACD same-job readback, and a full decorated controller with about 500000 XML elements and 14500 rungs. These are retained-job simulations, not native SDK or customer acceptance results.
