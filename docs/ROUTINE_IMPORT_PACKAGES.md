# Whole-routine import packages

`studio_prepare_routine_import_package` prepares an offline whole-routine L5X, a separate prerequisite manifest and a ZIP. REST clients use `POST /api/routine-imports/prepare`; the desktop **Extensions… → Logix** operation calls the same service. It supports readable, unprotected RLL and ST routines. It creates artifacts without creating tags, opening a native project or connecting to a controller.

## Dependency scope

| Mode | Routine L5X | Create before import |
|---|---|---|
| `tag_definition_free` — default fallback | One full routine and its controller/program context names. No `Tag`, `Tags` or saved tag-value elements. | Every explicitly selected missing program/controller tag, with exact scope, type, dimensions and initial data. |
| `program_only` — optional preparation mode | One full routine plus explicitly selected new program helpers under `Program/Tags/Tag`. No controller tag definitions or existing tag values. | Controller parameters in `controllerCreateBeforeImport`; new program dependencies are listed separately in `bundledProgramDependencies`. |

Rockwell documents referenced program/controller tags in routine exports and import configuration for those dependencies. Program tags use the program's `Tags` collection. This supported component syntax does not establish that a generated file will be accepted by a particular native application/version. See [1756-PM019, routine and referenced-tag import/export](https://literature.rockwellautomation.com/idc/groups/literature/documents/pm/1756-pm019_-en-p.pdf) and [1756-RM014, program/tag structure](https://literature.rockwellautomation.com/idc/groups/literature/documents/rm/1756-rm014_-en-p.pdf).

User-reported fatal Studio failures have involved both a generated routine file containing a new controller parameter and another containing only program-scoped scalar helpers. The reports are retained as reports; XML/package checks did not establish native acceptance or an exact root cause. **Program-only packaging is not a verified fix.** Use the tag-free fallback when tag creation must happen separately. Do not replay a failed or uncertain customer import to diagnose it, or generalize a particular failure to every Studio version or every tag dependency.

## Prepare a package

Register the modified L5X and a separate destination/before-change L5X baseline. Pin both managed IDs and SHA-256 hashes. They must identify the same controller and selected program. Select one exact routine and explicitly list only the new tags needed by this change:

```json
{
  "source": { "projectId": "MODIFIED_ID", "sha256": "MODIFIED_SHA256" },
  "baseline": { "projectId": "BASELINE_ID", "sha256": "BASELINE_SHA256" },
  "programName": "App",
  "routineName": "Main",
  "mode": "tag_definition_free",
  "prerequisiteTags": [
    { "name": "HelperP", "scope": "program" },
    { "name": "RequestedSetpoint", "scope": "controller" }
  ]
}
```

In MCP's extension interface, pass this body as `arguments.request` through the operation's described apply channel and exact current generation. The result contains `routine`, `prerequisites` and `package` artifact IDs/hashes, `requiredTags`, `validation`, the parent `programXPath` and exact `targetName`. Save artifacts to new paths with the normal artifact tool. The ZIP contains `routine.l5x`, `prerequisites.json` and `README.txt`.

Prerequisites retain the source type, scalar/array dimensions, relevant creation attributes and every supplied initial `Data` representation. XML strings preserve integer/REAL literals without conversion through floating point. Missing initial data is rejected rather than guessed. An explicitly selected tag already present in the pinned baseline is rejected, including case-insensitive name matches: its saved values must never enter the manifest or dependency payload. Empty selections are allowed when no new tags need packaging. This is an explicit selection, not a compiler-bound alias/AOI/UDT/indirect dependency closure; required types and other dependencies must already exist or be handled separately.

For `program_only`, selected missing program helpers are copied as sibling tag dependencies of the full routine in the exact program context. Controller definitions are never copied. If a bundled helper now exists in the actual destination, preserve its values and regenerate against a fresh baseline without that definition, or use tag-free packaging. Do not rely on XML checks or assume a collision will preserve values under every native import configuration. Aliases, produced/consumed tags, forced tags, safety and opaque routines are outside this preparation profile.

Limits are 128 selected prerequisites, 4 MiB routine XML and 2 MiB prerequisite JSON. The original snapshot/source bytes and revisions remain unchanged. Both modes exclude saved values of baseline-existing tags; logic constants and comments remain part of the full routine. Changed logic can still write tags when executed, so this exclusion is not a functional/runtime guarantee.

## Create prerequisites, replace the full routine, finalize

1. Review `requiredTags.createBeforeImport` and create **only tags still missing**, in the exact scope, type and dimensions, with the listed initial values. Verify creation before importing. For an already existing tag, preserve its current values and validate its layout; never reinitialize it.
2. With the tag-free fallback, all missing tags are created separately. With `program_only`, create the separate controller parameters first and verify that bundled program helpers are still missing. No tag creation is performed by this preparation tool.
3. Use a matching saved ACD and the package's exact routine artifact/hash for `studio_online_edit_plan`, with `importKind:"routine"`, `routinePackagingMode` matching the chosen mode and `workflow:"finalize_during_import"`. `program_only` also requires `routineTagBaseline:{"projectId":"BASELINE_ID","sha256":"BASELINE_SHA256"}` so planning can reject existing-tag definitions again. Require a returned `routinePackageValidation` matching the requested mode; an older adapter/service that drops new fields has not applied this guard. The tag-free planner rejects all tag definitions, containers and saved-data elements, including hidden/namespaced ones.
4. Import/replace at the **parent program XPath plus exact routine target name**. Targeting an existing RLL routine itself with the with-target API can append rungs. Choose SDK `FinalizeEdits` on the initial authorized import; it provides no separate Test Edits interval, and documented assembly depends on controller mode. Do not change mode or reimport to force an outcome.
5. Verify native acceptance, exported full-routine/rung/line counts, edit state and saved-value preservation separately. Keep the source/baseline, prerequisite list, package hashes, attempted calls and native observations. XML/schema checks, package validation and SDK success are separate evidence levels. Neither mode sets `nativeImportAccepted` to true.

See [online-edit planning and finalization](ONLINE_EDITS.md) for controller authorization, correlation, existing accepted edits and native acceptance boundaries. A stopped/interrupted/indeterminate operation is not retried automatically.

## Deployment boundary

The operation requires a compatible shared-service build and Logix extension. The fixed typed online-plan tool also needs a compatible frontend to forward the new fields; a current extension operation can carry its advertised request schema. If the installed catalog does not advertise support, changing only the frontend or adding request fields does not install the new backend contract. Deploy it through the normal reviewed maintenance workflow; do not restart an active service or replay native work just to try the feature. Source/fixture checks are not installed-route or native acceptance.
