# Whole-program import packaging

Discover `studio_prepare_program_import_package` through the current Logix extension catalog and describe it before using its apply channel. It creates three immutable artifacts: program L5X, manifest JSON and a ZIP containing both plus native handoff instructions. It never imports, connects, inserts rungs, creates a native job or changes a managed source revision. The GUI's **Extensions…** operation editor uses this same service operation.

Pin `source:{projectId,sha256}` and `baseline:{projectId,sha256}`, exact `programName`, and the existing `taskName`. The source is a complete program/controller snapshot; the destination baseline is a complete controller snapshot with a unique existing scheduled-program location. Software revision and controller name must match. Safety programs, opaque routines, pending/test/unknown content variants, local-tag layout/alias changes and omitted baseline routines/tags are refused. Readable RLL/ST/FBD/SFC normal or finalized exports retain their content. No edit container is selected or stripped to manufacture finalized logic.

The optional `changedRoutines` list is an assertion about changed/new routines, not an inclusion filter. A program with 17 changed routines and 29 total routines still includes all 29, along with every local tag. Overwriting a program can delete destination routines and local tags absent from the import. This profile rejects those omissions. See [Rockwell 1756-PM019, programs](https://literature.rockwellautomation.com/idc/groups/literature/documents/pm/1756-pm019_-en-p.pdf#page=11).

`newControllerTags` and `newDataTypes` are explicit new-only selections. Existing controller tags/types, unrelated hardware, tasks, clocks, other programs and AOI definitions are excluded from the import. Referenced existing datatype layouts must agree; new datatype dependencies must be explicitly selected. Lexical direct RLL/ST/local-alias references check known controller-tag selection and destination declarations. This is not compiler-bound coverage of FBD/SFC, AOI, alias propagation, indirect or every logic reference. Review those against the destination. Source routine/local-tag and selected dependency XML bytes are preserved; only the target Program's `Use` envelope metadata may change. Raw and canonical hashes are recorded separately.

Example request after registering generated or authorized snapshots:

```json
{
  "source": { "projectId": "source-id", "sha256": "source-sha256" },
  "baseline": { "projectId": "baseline-id", "sha256": "baseline-sha256" },
  "programName": "App",
  "taskName": "MainTask",
  "newControllerTags": ["NewGlobal"],
  "newDataTypes": ["NewType"],
  "schema": {
    "path": "C:\\Schemas\\L5X.xsd",
    "sha256": "exact-schema-sha256",
    "scope": "partial_definitions"
  }
}
```

Schema is optional. Without it, `structure_only` reports `xsdApplied:false`; it is not an XSD pass. A selected local self-contained schema requires its exact hash, configured source-path access and a maximum size of 4 MiB. No DTD, external include/import/redefine or network resolution is allowed.

`partial_definitions` compiles the unchanged full schema and locates its original declarations at `RSLogix5000Content/Controller/Programs/Program`, `.../Tags/Tag` and `.../DataTypes/DataType`. It validates the actual, unchanged named definitions against those declarations, including nested local tags/routines. It does not remove attributes, relax required elements, edit the schema or add fake controller state. A V35 full-controller schema requiring Modules, Tasks and clock containers can reject the partial context wrapper while the selected definitions validate. The report explicitly leaves `fullDocumentValidated:false` and native wrapper/import acceptance pending. `full_document` instead applies the original full schema to the entire XML and refuses its errors. The generated regression schema demonstrates this distinction; it is not an installed V35 vendor schema. The implementation uses [.NET subtree schema validation](https://learn.microsoft.com/en-us/dotnet/api/system.xml.schema.extensions.validate?view=net-8.0).

For authorized native import, choose the complete Program overwrite, its exact existing scheduled location and reviewed main/fault assignments. Keep **Preserve existing tag values in offline project** selected. Existing online data-table values are preserved by the native import contract; new tags receive imported initial values. Layout conversion, renamed tags and non-data-table-backed configuration have separate behavior. See [Studio help, Program Tags](https://www.rockwellautomation.com/en-gb/docs/studio-5000-logix-designer/37-00/contents-ditamap/studio-5000-logix-designer/import-and-export/import-paste-config-program-tags.html). When a no-test-interval workflow is authorized, choose native program finalization during that import and separately observe the resulting edit state. Packaging itself provides no native acceptance evidence.

A new UDT addition does not inherently require download. Overwriting an existing referenced UDT is restricted online; this profile excludes existing UDT overwrites. See [1756-PM019, user-defined types](https://literature.rockwellautomation.com/idc/groups/literature/documents/pm/1756-pm019_-en-p.pdf#page=14).

Use `studio_program_import_readback` with `manifestArtifactId`, `expectedManifestSha256` and a pinned `readback` snapshot. It reuses the retained manifest and original pinned inputs. It compares all routines and local-tag declarations, new dependencies, program assignments, the baseline task/schedule and other controller payloads. Appended/duplicated rungs fail exact routine comparison. Default `valueMode:"offline_preserved"` compares existing local values to the destination baseline and new-tag initial data to the source. `online_declarations` checks declarations/configuration while explicitly returning stored/live values unverified. A matching saved export does not prove that a native import/finalization occurred or that running logic preserved runtime values.

Keep prior semantic/compiler/native evidence separate. Reuse it only for matching exact source, routine and type hashes; do not rerun semantic fixtures or native imports merely to package unchanged reviewed bytes. Limits: UTF-8 inputs up to 64 MiB, import XML up to 32 MiB, 1..1024 routines, up to 4096 local tags and 256 selected new definitions per dependency kind. Original projects, values and receipts remain retained.
