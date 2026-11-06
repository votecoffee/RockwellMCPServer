# Merge reviewed logic while keeping current saved values

Use this workflow for “merge logic but keep current values”, “carry reviewed routines into the latest saved export” and preservation/readback checks. Discover the existing operations before writing a custom merge or validation script. Inspect the installed operation schemas: the whole-project `preserve-fresh` mode and validation profiles require v0.30 or later. An older installed service cannot acquire these features from a guide alone.

## Select the three immutable inputs

Register separate managed copies and retain each returned project ID, revision and SHA-256:

| Role | Meaning |
|---|---|
| `baseline` | Common ancestor of the reviewed logic and fresh export. |
| `logic` | Reviewed routines and annotations derived from that baseline. |
| `values` | Latest **saved** L5X export whose complete tags, representations and configuration should remain authoritative. |

Prefer an immutable `source_snapshot` from an evidence packet over a path that may have changed. Check its supplied hash. A saved export is not simultaneous live state. Capturing a controller upload is a separate, explicitly authorized SDK workflow; this merge never connects.

## Discover, preview and review

In the standalone CLI, use `operation-help studio_logic_value_merge_preview`, `operation-help studio_logic_value_merge_apply` and `operation-help studio_validation_preview` to check the installed schemas. For the extension host, discover and describe the same operations, then use their declared effect channels and pin the current generation.

The preview request uses `mode: "preserve-fresh"`:

```json
{
  "baseline": {"projectId": "<baseline-id>", "sha256": "<baseline-sha>"},
  "logic": {"projectId": "<reviewed-id>", "sha256": "<reviewed-sha>"},
  "values": {"projectId": "<fresh-id>", "sha256": "<fresh-sha>"},
  "mode": "preserve-fresh",
  "configurationFields": []
}
```

This profile starts from the fresh file. It preserves all controller/program tags, aliases, definitions, stored L5K/decorated/String/native representations and other configuration bytes. It applies reviewed whole **program routines**, including annotations, additions and removals. It never decodes one value representation and replaces another. Fresh code that differs from both baseline and reviewed logic blocks the merge.

If reviewed MESSAGE configuration changed, inspect each field's baseline/logic/fresh values and hashes. Add only reviewed exact choices, for example:

```json
"configurationFields": [
  {"tagName": "MessageToHost", "attribute": "RemoteElement", "choice": "logic"},
  {"tagName": "MessageToHost", "attribute": "ConnectionPath", "choice": "logic"}
]
```

`programName` selects a program-local MESSAGE tag; omit it for controller scope. `logic` takes that attribute from the pinned reviewed input; `fresh` explicitly retains it. No field value is invented. Other MESSAGE fields, including saved runtime fields, stay fresh. Re-preview after every choice change.

To add declarations, first review the blocked preview's named additions. Approve each new tag in its exact scope and each new UDT by name, including all transitive new dependencies.

```json
{
  "tagAdditions": [
    {"tagName": "NewCounter", "startupValues": "native-default"},
    {"tagName": "NewRecords", "programName": "ExampleProgram", "startupValues": "reviewed-logic"}
  ],
  "dataTypeAdditions": ["RecordType", "NestedMemberType"]
}
```

Use `reviewed-logic` to copy every explicit saved startup representation from the reviewed tag. The receipt lists format, byte count, SHA-256 and a bounded preview. Use `native-default` only when the reviewed tag contains no `Data` and the absence of a saved startup value is intentional. The receipt lists UDTs in dependency order, independent of the order of approval. Existing names and layouts remain protected, including local additions that would shadow a controller tag. Use a separate workflow for alias, safety, produced/consumed, forced or MESSAGE tag additions.

Review `findings`, `conflicts`, `preservation`, `policySha256` and `reviewSha256`. The receipt counts tags, every representation, representation differences from the reviewed copy, routine replacements/additions/removals and applied MESSAGE fields. `canApply` must be true. Type/array/alias/UDT/AOI layout conflicts, safety routine edits, program identities and reviewed non-routine configuration changes are not silently resolved. Reviewed tag descriptions remain fresh in this profile too. Do not use a broad MESSAGE replacement or tag-value resolution to bypass a layout/configuration conflict.

Budget: UTF-8 L5X, 64 MiB and 1,000,000 XML elements per input, 64 nesting levels, 10,000 findings and 256 MESSAGE field choices. Untouched source bytes, CDATA and line endings survive lexical edits. This is separate from legacy `mode: "three-way"`, which transfers up to 256 selected compatible standard base-tag values into modified logic and may remove competing destination representations. The legacy mode is not the whole-project current-values workflow.

## Apply once to a separate managed project

Pass the identical preview request and its exact `expectedReviewSha256` to `studio_logic_value_merge_apply` through the apply channel. It rechecks all pinned inputs and creates a new managed project. The three originals stay unchanged. Retain the result ID/hash. A lost response is not permission to replay; inspect managed projects/audit evidence before a deliberate retry.

For a policy with declaration additions, run `studio_validation_preview` with `profile: "merge-preservation"`, the candidate, the identical merge policy and `expectedPolicySha256`. Its `exact-merge-bytes` check rebuilds the approved lexical patch and compares the entire saved result SHA-256. The check covers existing representations, configuration and addresses, new startup bytes and all unselected bytes. Use `native-readback` separately when an operator supplies a native re-export that may have different formatting.

## Validate the expected change policy

Read `studio_validation_profiles`. Choose the smallest relevant named profile:

| Profile | Inputs and supported checks |
|---|---|
| `merge-preservation` | Candidate plus the same merge request and its `expectedPolicySha256`. Checks all reviewed program routines/annotations, fresh tag identities/definitions/aliases, every stored representation, exact MESSAGE exceptions and all untouched module/task/safety/AOI configuration. |
| `offline-integrity` | Candidate and optional baseline. Checks program main-routine references and literal RLL `JSR` targets. Reports new, inherited and resolved findings. This is not compiler/AOI/dynamic binding. |
| `native-readback` | Candidate is an **already supplied native readback**; reference is the intended saved project. Checks program/readable-AOI routines, tags, all representations and remaining configuration. It does not start native jobs or authenticate native provenance. |

For preservation, call `studio_validation_preview` with:

```json
{
  "profile": "merge-preservation",
  "candidate": {"projectId": "<merged-id>", "sha256": "<merged-sha>"},
  "merge": {
    "baseline": {"projectId": "<baseline-id>", "sha256": "<baseline-sha>"},
    "logic": {"projectId": "<reviewed-id>", "sha256": "<reviewed-sha>"},
    "values": {"projectId": "<fresh-id>", "sha256": "<fresh-sha>"},
    "mode": "preserve-fresh",
    "configurationFields": []
  },
  "expectedPolicySha256": "<merge-preview.policySha256>"
}
```

Use the identical merge object, including its reviewed MESSAGE choices. Check each result's status, exact locations/hashes, bounded value/text snippets and truncation flag. A profile pass means no unexpected failure **within its stated scope**. Inherited reference findings remain visible; they are not new defects. Opaque values are preserved/compared, not decoded to establish cross-representation semantic equivalence. Safety semantics and runtime/physical acceptance remain unsupported.

All four validation profiles also expose `complex-udt-member-radix`. For declared UDT members and the listed built-in structures (including STRING), the source metadata convention is `Radix="NullType"`, for scalars and arrays. A custom string definition is a declared UDT; its numeric LEN/DATA members keep their atomic display styles. Explicit numeric or missing radix on a known complex member fails, including when inherited; unknown member types are reported unsupported. Findings retain exact managed snapshot hashes, member locations and canonical member hashes. This is separate from XSD validation: a schema can allow Decimal without constraining its relationship to DataType. It does not prove the cause of a native import failure or authorize a retry. No automatic metadata normalization occurs. Read the advertised `memberMetadataProfile` for the exact supported type lists; native preconfiguration/import acceptance remains separate. Repository regression fixtures are generated convention examples, not captured native exports or native acceptance receipts.

For native readback, strict comparison is the default. Explicit `nativeRules` can normalize MESSAGE connection-path whitespace/boolean case, L5K whitespace outside quoted strings, RLL operand whitespace outside quoted strings, or ignore display rung numbers. `normalizeAnnotationBoundaryWhitespace:true` is a separate opt-in for surrounding XML space/tab/CR/LF in scalar `Comment`/`Description` fields outside stored/encoded data. It retains every affected field as a `pass`/`normalized` finding with exact field hashes and bounded before/after text. Internal spacing/newlines, Unicode whitespace such as NBSP, attributes, markup, ST/RLL code and saved values remain significant. This comparison rule edits no input. It also applies to `legacy-review-readback`; neither merge-preservation nor offline-integrity accepts native rules. Native exporters may alter other metadata; do not infer whole-project preservation from a code-only check.

Review other intended native differences individually using `expectedDifferences`: exact `check`, `location`, `expectedSha256`, `actualSha256` and a nonempty `reason` copied from a reviewed strict result. The maximum stays **1000** exact exceptions; normalized findings do not consume or expand that budget. No wildcard or stale field hash is accepted. Re-preview; accepted differences remain visible as `expected`, with counts in their checks. This policy cannot waive unsupported native/safety/runtime claims and is not available to conceal merge-preservation drift.

## Retain evidence, then export separately

Call `studio_validation_record` through apply with `{preview: <the exact validation request>, expectedReviewSha256: <validation-preview.reviewSha256>}`. It recomputes the checks and retains an immutable JSON artifact containing the full policy, all source/candidate hashes, tool version, checks/findings, expected differences and unsupported coverage. Failed/unsupported reports can be retained too. Keep the artifact ID/SHA; re-read it with `studio_validation_receipt`, or save it with `studio_save_artifact`. Receipt creation changes no engineering project.

Export the merged project to a **new** L5X/ACD artifact through existing export operations. If native evidence already exists, compare its saved files rather than rerunning jobs. If separately requested and supported by the installed SDK, prepare a private offline import/save/export/readback through the current SDK catalog. Inspect exact source/output hashes and native events; do not equate save/import success with compile/Verify Controller or runtime acceptance. Never invent a v35 build/edit-finalization API. Controller download, mode changes and physical tests remain separate authorization and acceptance work.

When the user requests a named L5X import copy, export/save it to the requested allowed folder as soon as the authorized merge and required scoped checks finish. Preserve the original, read back the saved bytes against the artifact SHA and report additional checks separately. ACD creation and native SDK import/build are separate work; respect user-owned Studio import. If MCP is unavailable and the user asks to continue through the installed service, follow the [authorized REST fallback and delivery workflow](structural-edits.md#authorized-rest-fallback) with the same review/hash/receipt guards. Retain/reuse unchanged valid validation rather than rerunning it solely for handoff.

The GUI exposes these same service operations under **Diagnostics & workflows → Merge logic, keep current values** and **Preservation and integrity validation**, with a separate candidate/readback selector. Copy the returned review/policy hashes into the prepared editors; no action starts polling or native work automatically.
