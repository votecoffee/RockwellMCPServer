# AOI dependencies and original-fresh preservation

Use this offline workflow to carry readable standard AOI dependencies into a fresh full controller export while keeping its existing tags, saved values, modules, addresses and safety configuration. It uses shared Core operations and the existing reviewed structural apply path. The standalone CLI, MCP/API and **Diagnostics → AOI dependencies and fresh preservation** expose the same operations.

No native SDK job, import, controller connection or deployment is performed. Native acceptance remains separately authorized engineering-owner work. An engineering plan and explicit saved-state decisions still govern the structural batch; generating a dependency plan does not authorize it.

## Plan explicit dependencies

Register preserved copies of the reviewed source and original fresh export. Both must be full controller L5X files (`TargetType="Controller"`, `ContainsContext="false"`), already retargeted to the same controller and software revision. Discover the exact installed schemas before preparing requests:

```powershell
& $cli operation-help studio_aoi_dependency_plan
& $cli operation-help studio_structural_edit_preview
& $cli operation-help studio_structural_edit_apply
& $cli operation-help studio_preservation_chain_review
```

Call `studio_aoi_dependency_plan` with the two exact managed project hashes, selected root AOIs, and every approved new AOI/UDT identity. For example, the synthetic dependency graph is `Link → Nested → Outer → Inner` plus compatible existing `Helper`:

```json
{
  "workspace": "review",
  "operation": "studio_aoi_dependency_plan",
  "arguments": {
    "request": {
      "source": { "projectId": "<reviewed-source-id>", "sha256": "<source-hash>" },
      "destination": { "projectId": "<original-fresh-id>", "sha256": "<fresh-hash>" },
      "roots": ["Link"],
      "aoiAdditions": ["Link", "Nested"],
      "dataTypeAdditions": ["Outer", "Inner"]
    }
  }
}
```

The planner orders transitive declared UDT/AOI dependencies before their consumers. It reads parameter/local tag datatypes and supported RLL instruction calls. A source definition must exist even when a destination has the same name. Existing AOI dependencies require matching complete normalized definitions; UDT dependencies require matching member layouts. Compatible existing dependencies are **Use Existing**, never overwritten. Unrelated existing and protected AOIs stay untouched. Missing approvals, identity/namespace collisions, cycles, incompatible dependencies, unknown instructions and missing source definitions block the plan.

This planner accepts standard readable unsigned RLL AOIs and populated declared UDTs. New or transitive protected, signed, safety, ST/FBD/SFC definitions require a separate supported review; incomplete-binding acknowledgment does not override the planner. Supported lexical coverage does not establish compiler binding, native compatibility or commissioning acceptance.

A passing result returns `definitions`, `referenceAdditions`, `edits`, `dataTypeScope`, and an immutable `planReceipt`. Each reference binds an immutable snapshot of the complete source and the raw selected element hash. A blocked result retains findings and has no executable edits. The source and destination remain unchanged.

## Review and apply a structural batch

Build one `studio_structural_edit_preview` request against the **original fresh project**:

- Pin its `projectId`, `expectedSha256` and `expectedRevision`.
- Bind the reviewed plan with `plan: {"artifactId":"<planReceipt.id>","sha256":"<planReceipt.sha256>"}`. The plan is hash-checked evidence, not interpreted authorization.
- Copy the returned `referenceAdditions` into `scope.referenceAdditions`, `dataTypeScope` into `scope.dataTypes`, and `edits` into the edit list.
- Declare exact routine/tag scopes and any additional reviewed logic edits. Whole-tag additions require explicit `initialize-reviewed` or other applicable value dispositions and reviewed startup data. Existing tag values and configuration remain fresh.
- Set `safetyHandling:"block"` for the ordinary batch and explicitly acknowledge the reported incomplete compiler/external bindings when authorized.

Review the complete preview. Apply only its unchanged request, `reviewSha256`, preview receipt ID and receipt SHA. Existing library identities cannot be replaced or upgraded. Structural insertion also checks transitive AOI contracts, so a same-name nested AOI is insufficient even when this planner was not used. The operation publishes a separate REVIEW ONLY project, immutable L5X and apply receipt. Save artifacts to new absolute paths using `studio_save_artifact`.

If an engineering owner separately authorizes safety-routine overlays, retain that explicit scope and `safetyHandling:"review-only"` in the structural request. Modules, SafetyNetwork configuration, signatures and unselected data remain protected. General preserve-fresh merging keeps its existing safety/AOI-change restrictions; this dependency planner does not silently enable those changes. [Structural review and state decisions](../workflows/rockwell-engineering/references/structural-edits.md)

## Preserve numeric Ethernet AOI operands

An Ethernet AOI can encode an IPv4 address as four numeric operands. Use `studio_aoi_address_plan` for explicit per-device selection:

```json
{
  "fresh": { "projectId": "<fresh-id>", "sha256": "<fresh-hash>" },
  "logic": { "projectId": "<reviewed-logic-id>", "sha256": "<logic-hash>" },
  "pins": [{
    "programName": "P", "routineName": "Main",
    "instruction": "EthernetDevice", "instanceOperand": "Dev",
    "argumentIndices": [1, 2, 3, 4]
  }]
}
```

This is the body inside `arguments.request`. Index zero is the instance operand; the four indices are zero-based call argument positions. The example maps `EthernetDevice(Dev,192,0,2,44)` to four address octets. The exact instruction/instance must occur once in each selected standard RLL routine. Only decimal literals from 0 to 255 are accepted. Expressions, symbolic values, radix forms, duplicate calls and overlapping selections are refused.

The result reports both sets of octets and exact `logicEdits` that replace only selected literals with the original fresh spelling. Whitespace and other reviewed logic remain intact. Review those proposed values when assembling the structural batch; no edit is applied automatically. There is no global address detection or device-name inference. Supply explicit pins for each relevant changed device call. Module configuration and MESSAGE/tag records are preserved independently as original fresh bytes.

## Compose the original-fresh receipt

After one or more completed structural stages, call `studio_preservation_chain_review` with:

```json
{
  "originalFresh": { "projectId": "<original-fresh-id>", "sha256": "<original-fresh-hash>" },
  "candidate": { "projectId": "<final-candidate-id>", "sha256": "<candidate-hash>" },
  "stages": [{ "artifactId": "<structural-apply-receipt-id>", "sha256": "<receipt-hash>" }],
  "addressPins": [{
    "programName": "P", "routineName": "Main",
    "instruction": "EthernetDevice", "instanceOperand": "Dev",
    "argumentIndices": [1, 2, 3, 4]
  }]
}
```

Order stage receipts from the original fresh export to the final candidate. The review verifies their artifact hashes and completed durable apply states, rechecks pinned source/plan/reference inputs, independently rebuilds every patch, and checks exact project/hash links. It then compares every existing original-fresh tag record and protected module/task/force/safety/AOI subtree as raw bytes. Every selected address pin must retain its exact fresh octets. All other unselected bytes are covered by the independently rebuilt structural stages.

A safety overlay additionally requires `safetyOverlayReason` explaining the separate explicit authorization; its retained structural stage must already use review-only safety handling. The composed receipt does not authorize or apply a safety edit. A separately reviewed existing-tag replacement can be valid structural work while failing this stricter original-fresh preservation profile.

The result includes `canAttest`, stage identities, preserved-record counts, findings and an exportable immutable JSON `receipt`. Its top-level `original_fresh_sha256` and `candidate_sha256` bind the two exact exports. Retain the receipt SHA and all stage receipts. A blocked review emits evidence with `canAttest:false`; it is not a preservation pass. Unsupported/reordered/missing/indeterminate stages are never replayed or treated as equivalent. Only completed structural apply receipts are supported; native job or generic merge results are not silently adopted as chain stages.

## Bounds and validation

Inputs use the shared 64-MiB / 1000000-element / 64-level XML limits. A plan supports 32 roots, 128 new AOIs, 128 new UDTs, 256 closure definitions, 32 dependency levels, 16 MiB logic text, 1 MiB per inserted definition, 4 MiB inserted text and a 1-MiB plan receipt budget. The RLL parser retains its rung/instruction budgets. Address planning supports 64 explicit pins and 1 MiB text per selected routine. Composition supports eight stages, 256 MiB total reviewed input bytes and 2000 findings. Oversized reviews fail without a partial attestation.

Generated fixture tests cover ordered dependencies, UTF-8/UTF-16 exact reversal to fresh bytes, populated saved representations, protected libraries, modules/SafetyNetwork records, dependency collisions/mismatches, explicit numeric address carry-forward, tampering, interrupted apply state and separately reviewed safety overlays. These are offline tool checks; no customer project, native import, controller or deployment acceptance is implied.
