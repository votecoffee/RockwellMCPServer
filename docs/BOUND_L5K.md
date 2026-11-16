# Bound native L5K export

Use this workflow to supply GM with an exact native L5K export and an immutable JSON binding to the selected saved L5X. It uses Rockwell's native `SaveAsAsync` on one private, pinned project session. Keep the SDK input hash, native L5X readback hash and native L5K hash separate. GM owns validation for each original utility and its parser.

## Plan and run once

Use the same CLI workspace for all steps. Discover the exact schema with `operation-help studio_bound_l5k_plan`, `operation-help studio_sdk_start_job`, `operation-help studio_bound_l5k_preview` and `operation-help studio_bound_l5k_record`. A named operation takes its body inside `arguments.request`.

1. Call `studio_bound_l5k_plan` with an absolute `sourcePath`, exact `expectedSha256`, and `majorRevision` for the reviewed installed Logix engine. The source must be one saved ACD or a full controller L5X. For L5X, the plan validates the full export and its version before preparing the SDK request.
2. Obtain explicit authorization to run the single offline native job. The local SDK 2.1.974 bridge, installed and licensed matching Logix Designer engine, and running Logix Designer SDK Service are prerequisites. The paired export does not start the vendor service automatically.
3. Pass the returned `sdkRequest` unchanged to `studio_sdk_start_job` in an authorized `allowNative: true` invocation. Give it an appropriate bounded CLI timeout and omit any controller target. The private job copies and rehashes the source, opens the ACD or imports the L5X via the matching native open path, and saves detailed `native-readback.l5x` and `native-export.l5k` from that one offline session. Keep the returned SDK job ID and both artifacts. An indeterminate or failed job requires inspection with `studio_sdk_get_job` and the retained lifecycle evidence before further native dispatch.

The plan, preview and record commands work with native execution disabled. Only the SDK job starts a native worker.

## Bounded offline ACD-to-L5X

This paired workflow is also the supported installed CLI route when only a detailed native L5X is needed from an ACD. It exports both formats without needing a scalar tag name or `native-pilot.json`. Use a separate review workspace, one exact saved source, its SHA-256 and the intended matching Logix major revision. A plan hashes the ACD but does not authenticate its native format or installed engine. Native open/readback remains the version-specific acceptance check.

First discover `operation-help run`, `studio_sdk_service_status`, `studio_bound_l5k_plan` and `studio_sdk_start_job`. Read SDK service status without `allowNative`. If the service is stopped, have its owner start it, or use the separately authorized `studio_sdk_service_ensure_running` request with a retained request ID and private workspace recovery permission. Starting the service is separate from exporting; this workflow never starts it automatically or changes its startup configuration.

Save the following plan request as UTF-8 JSON and invoke `run --request` with its absolute path:

```json
{
  "workspace": "offline-export-review",
  "operation": "studio_bound_l5k_plan",
  "arguments": {
    "request": {
      "sourcePath": "C:\\Reviews\\Saved.ACD",
      "expectedSha256": "<exact saved ACD SHA-256>",
      "majorRevision": 35
    }
  }
}
```

Review the plan and obtain explicit offline native authorization. Copy its complete `sdkRequest` object, unchanged, into `arguments.request` in a new request:

```json
{
  "workspace": "offline-export-review",
  "allowNative": true,
  "operation": "studio_sdk_start_job",
  "arguments": {
    "request": {
      "calls": [
        { "method": "OpenLogixProjectAsync", "arguments": {} },
        { "method": "SaveAsAsync", "arguments": { "savePath": "native-readback.l5x", "detailedL5x": true } },
        { "method": "SaveAsAsync", "arguments": { "savePath": "native-export.l5k" } }
      ],
      "sourcePath": "C:\\Reviews\\Saved.ACD",
      "expectedSha256": "<exact saved ACD SHA-256>",
      "boundL5kMajorRevision": 35
    }
  }
}
```

Invoke the native request with an explicit bounded deadline:

```powershell
& $cli run --request 'C:\Reviews\native-export.json' --timeout-seconds 300
```

CLI 2.5.2 also defaults native-enabled workspace runs to 300 seconds. The SDK worker receives a 270-second limit with the remaining 30 seconds reserved for cleanup. This is a bounded, owned invocation, not a durable background service. Retain the outer job ID/receipt, SDK job ID, pinned source hash, SDK assembly and output artifacts. Omit controller targets entirely. The paired profile rejects communications paths and controller calls, and requires the native session to remain Offline.

On success, inspect the completed SDK job and use `studio_save_artifact` to export its detailed L5X to a new absolute path. Preserve the ACD input hash and L5X output hash as distinct provenance. For a GM same-snapshot binding, continue with preview/record below and select the native L5X hash.

If a native request times out, retain `recovery.sdkJobReferences`, the outer intent/result, the SDK job record, lifecycle checkpoints and pending markers. A parent deadline can leave the SDK job indeterminate when its owner next opens the workspace. Inspect with `studio_sdk_get_job` and the linked files using native execution disabled. Empty recorded steps or confirmed process cleanup do not prove the SDK never opened the project, saved an output or closed its native session. Do not retry, clear holds, or adopt partial output until the owner has reviewed the exact job and its native close evidence. This workflow provides no automatic retry or automatic reconciliation.

## Review the exact source and pair

Call `studio_bound_l5k_preview` with `jobId` and `selectedL5xSha256`.

- For an SDK input that was already a full L5X, choose its original hash to request a binding to that exact source. The preview requires whole-document structural equivalence to the native L5X, retaining values, code, comments, ordering and configuration. It allows XML serialization differences such as attribute order, formatting between elements and the exporter timestamp fields. The source and native hashes stay separate even when this check passes.
- For an ACD input, choose the exact native L5X artifact hash from the job. This is the L5X snapshot GM must use. Keep the saved ACD hash as native input provenance.
- If importing an L5X changes significant content, the preview withholds the original-source binding and reports available component differences. Retain the exact artifacts and review the differences. If the engineering owner adopts the native readback as a new saved source, explicitly preview its hash and use that exact L5X in GM. This binds the new snapshot without asserting equivalence to the prior file.

The review checks the completed offline job, its recorded SDK assembly, private input hash and both native artifact hashes. It also checks native L5K CRLF and the supported quoted Alarm/Prompt/SL blocks and version-marker text against the selected L5X. A split or differently encoded annotation that cannot be matched is reported for review. The workflow preserves native bytes and never repairs `$L`/ `$N`, annotates version markers or makes changes to the original inputs. GM separately checks its utility-specific version routing, required sections and native Excel behavior.

## Retain and hand off

After reviewing the output, call `studio_bound_l5k_record` with the identical preview request in `preview` and its `reviewSha256` in `expectedReviewSha256`. A passing review returns the exportable L5K and L5X artifact IDs, an immutable JSON receipt artifact, and a ready-to-use `derived_input` block.

```json
{
  "derived_input": {
    "path": "C:\\Reviews\\native-export.l5k",
    "sha256": "<exact L5K SHA-256>",
    "binding": {
      "path": "C:\\Reviews\\bound-l5k.json",
      "sha256": "<exact receipt SHA-256>",
      "source_pointer": "/selected_l5x_sha256",
      "output_pointer": "/derived_l5k_sha256"
    }
  }
}
```

The returned paths initially point to managed artifacts. If GM needs copies inside its allowed roots, use `studio_save_artifact` for the L5K, receipt and, when selected, the native L5X. Use new absolute paths and update only the two paths in the returned block. Keep the hashes and pointers exactly as returned. A blocked review can also be recorded as evidence. It has no approved source pointer or `derived_input` and is unsuitable for a GM input binding.
