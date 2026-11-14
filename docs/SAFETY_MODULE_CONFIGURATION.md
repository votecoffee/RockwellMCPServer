# Safety module configuration and SCID

RockwellMCPServer provides a reviewed saved-file point-configuration workflow for **1734-IB8S series B / major 2**, using pinned installed AOP identity/layout/enums. It does **not** provide native AOP execution or module-SCID regeneration. A decoded configuration-byte edit is an **unvalidated review candidate**. Retaining the previous configuration signature does not establish that the changed payload is native-valid.

## Current support

| Route | Supported boundary |
|---|---|
| Module connection timing review | Changes existing RPI, network-delay multiplier and reaction-time attributes in a separate review copy. Preserves configuration payloads and signatures; performs no AOP or SCID recalculation. |
| Installed AOP/EDS parameter decoding | Can establish the meaning of a field for an exact profile/module revision. Does not establish how to regenerate its signature. |
| Saved 1734-IB8S/B point review | `studio_safety_io_profiles`, `studio_safety_io_profile_inspect`, `studio_safety_io_browse`, `studio_safety_io_preview`, `studio_safety_io_apply`, `studio_safety_io_status`. Pins source, configuration, profile files and before-values; preserves all unselected bytes and the original SCID tuple. Native validation remains required. |
| Native Logix Designer module properties | Rockwell documents automatic configuration-signature generation when configuring a safety I/O device through Logix Designer. This is a user-operated native handoff, not an automated service operation. |
| SDK import/build/save/export | Available for separately authorized offline project work. Completion does not prove that a modified safety-module payload caused AOP validation or SCID recalculation. |
| SDK safety-signature methods | Operate on the controller/project safety signature. They are not a module-configuration SCID generator. |

The SDK statement is scoped to the repository's frozen `RockwellAutomation.LogixDesigner.LogixProject.CSClient` assembly **2.1.974.0**: 67 signatures, 63 unique method names. That surface has no dedicated AOP parameter-edit or module-SCID recalculation method. A newer vendor package requires a fresh API review. See [SDK support](SDK.md) and the [frozen surface](../src/Studio5000.Core/SdkSurface.json).

## Payload, signature and safety-task signature are different evidence

For an export containing `ConfigCRC`, `ConfigTime` and `ConfigDate`, retain the complete tuple with its module configuration and exact source bytes. Rockwell describes a device configuration signature as an ID, date and time used to identify the configuration. This is separate from the controller's safety-task signature and from this application's SHA-256 file/review hashes. [Safety I/O Device Signature, 1756-UM543](https://www.rockwellautomation.com/en-us/docs/technical/logix5000/_online/1756-um543/controllogix-5580-and-guardlogix-5580-controllers-/safety-i-o-devices/safety-i-o-device-signature.html).

For example, even when an exact installed 1734-OBV2S AOP/EDS identifies a selected mode field's value `0` as `Not Used`, that establishes the field's interpretation only. It does not establish a CRC input domain, timestamp serialization, signature-generation algorithm or import-triggered recalculation. Do not generalize offsets or enums to another series, firmware or profile revision.

This integration has not established a documented SCID regeneration rule for hand-edited 1734-OBV2S L5X configuration data. The general import/export manual describes module `ConfigTag`/`ConfigData`; that is not a promise that importing changed bytes regenerates a consistent signature. See [1756-RM014D, Define a module component](https://literature.rockwellautomation.com/idc/groups/literature/documents/rm/1756-rm014_-en-p.pdf).

The same regeneration limitation applies to the new 1734-IB8S/B byte-review adapter. The reviewed installed XML files define configuration members and point enums; they do not define a supported zero-to-regenerate contract for the signature tuple. This tool preserves that tuple, explicitly marks changed configurations `unvalidated-after-parameter-change`, and returns `nativeAopRoundTripRequired: true`. It never zeros the tuple or substitutes a generic CRC. The documented native configuration workflow below remains the supported generation/validation handoff.

Do not substitute a generic CRC32, copy another module's signature, zero the tuple, or alter its date/time to force acceptance. XML validity, decoded enums, a successful import/build and a preserved old signature are separate observations. None alone establishes module configuration or safety acceptance. Changing channel bytes while preserving the old tuple must remain explicitly **native acceptance not performed/SCID validity unknown**.

## Documented native handoff

The following is a procedure for separately authorized, user-operated offline work. It is not a claim that this repository has executed or automated it.

1. Preserve a trusted native baseline and work in a separate ACD copy. Record Logix revision, controller type, module catalog/series/firmware, installed AOP/EDS revision and the original configuration/signature. Start from the trusted baseline rather than assuming that opening a byte-patched L5X repairs its signature.
2. In native Logix Designer, open the selected module's properties with its matching AOP. Set the requested channel modes through the profile's own controls and review coupled channel, test-output and diagnostic settings. Tab names and permitted combinations depend on the exact profile. Existing locks or signature restrictions are a handoff condition; this workflow does not automatically unlock safety, delete signatures or reset ownership.
3. Apply the intended change through that native configuration workflow, save the separate project, and record the resulting signature shown on its Safety view. Rockwell documents automatic generation on configuration through Logix Designer; it does not describe an MCP call or a generic CRC function. [Safety I/O Device Signature](https://www.rockwellautomation.com/en-us/docs/technical/logix5000/_online/1756-um543/controllogix-5580-and-guardlogix-5580-controllers-/safety-i-o-devices/safety-i-o-device-signature.html).
4. Reopen/read back the saved native copy and produce a fresh native module/project export. Retain the complete configuration payload, SCID tuple, native observations, software/profile versions, diagnostics and artifact hashes. Confirm the selected semantic changes and compare unselected channels, modules, signatures and other project data. Do not infer native provenance from a supplied filename or matching bytes alone.
5. Report this as the observed offline native configuration/readback result. Controller download, physical behavior, functional safety testing and commissioning remain separate work. Module-specific configuration is described in the [POINT Guard I/O manual, 1734-UM013](https://literature.rockwellautomation.com/idc/groups/literature/documents/um/1734-um013_-en-p.pdf).

## What remains unavailable

There is no maintained SDK/MCP/GUI operation that selects safety-channel modes through an AOP and returns a recalculated, independently read-back SCID. The GUI's module timing review is not that operation. `GenerateSafetySignatureAsync` does not fill this gap, and a generic offline tag-value write has not been established as an AOP configuration route.

Before adding automation, require a documented vendor interface or module/version-specific import contract, exact parameter serialization, native generation evidence and independent export/readback. A synthetic investigation would need an untouched native round-trip control and a known AOP-authored semantic change on preserved copies, retaining all warnings and complete before/after payloads/signatures. An observed result would apply only to that tested combination, not every module or Logix release. No such native validation was performed for this documentation.

Existing [module timing reviews](../workflows/rockwell-engineering/references/module-connections.md) continue to return review-only outputs with `nativeAcceptance:"not-performed"`. `safetyHandling:"review-only"` permits the bounded review; it does not enable AOP execution or certify a retained signature.

## Saved point review workflow

Use one standalone CLI workspace and inspect each operation's schema before making requests. The same Core handlers back REST, extension operation editors and **Diagnostics & workflows → Safety I/O point review** in the GUI build. Existing hosts need the new Core routes; reloading an extension alone cannot add them.

1. Run `studio_safety_io_profiles` to discover the installed `RA 1734S Discrete` identity/parameter XML, hashes, exact physical layout and enum keys. Vendor files are read locally and are not bundled with this repository. For separately pinned profile files/artifacts, use `studio_safety_io_profile_inspect`; its provenance remains caller-pinned, with installed provenance not attested.
2. Register a preserved L5X and call `studio_safety_io_browse` with `request:{projectId,expectedSha256,expectedRevision,moduleName,profile,expectedProfileSha256}`. Copy `profile` from discovery's `files` and `expectedProfileSha256` from its `sha256`. Decode requires the exact catalog/vendor/product identity, major 2, a series-B identity variant, `ConfigSize="90"`, and one scalar flat L5K 90-byte vector under ConfigData or ConfigTag. Other series, layouts, nested/typed aggregates and alternate/duplicate saved representations are rejected. A missing Series attribute is explicitly reported as inferred from the pinned major-2 identity.
3. Review input modes, test sources, test-output modes, byte offsets, pair modes, diagnostic/reserved bits and the separate original `ConfigCRC`/`ConfigTime`/`ConfigDate` values. `configurationSha256` hashes raw bytes, independently of the source-file and profile hashes. English labels are loaded from the profile; requests use its stable keys, such as `SafetyPulseTest`, `PowerSupply`, `NotUsed`, `None`, `T0` and `T1`.
4. Preview explicit edits and before-values using the shape below. Changes to paired input operation/delays are unsupported. A changed input must have Single pair operation. Pulse-test inputs require an assigned PulseTest output; this adapter requires source None for other input modes. MutingLamp is accepted only where the pinned output enum permits it. Coupled settings are checked, never changed implicitly. Unrecognized reserved bits in selected fields require native AOP review.

```json
{
  "request": {
    "source": {
      "projectId": "<registered-id>",
      "expectedSha256": "<exact source hash>",
      "expectedRevision": 0,
      "moduleName": "SafetyInput",
      "profile": {"identity": {"path": "<absolute identity XML>", "sha256": "<hash>"}, "parameters": {"path": "<absolute parameter XML>", "sha256": "<hash>"}},
      "expectedProfileSha256": "<discovered profile binding hash>"
    },
    "expectedConfigurationSha256": "<decoded raw-byte hash>",
    "inputs": [
      {"point": 6, "expectedMode": "NotUsed", "expectedTestSource": "None", "mode": "Safety", "testSource": "None"},
      {"point": 7, "expectedMode": "NotUsed", "expectedTestSource": "None", "mode": "Safety", "testSource": "None"}
    ],
    "testOutputs": [
      {"point": 2, "expectedMode": "NotUsed", "mode": "Standard"},
      {"point": 3, "expectedMode": "NotUsed", "mode": "PowerSupply"}
    ],
    "safetyHandling": "review-only",
    "signatureHandling": "preserve-unvalidated"
  }
}
```

5. Review `changes`, Before/After decoded values, source/configuration/profile hashes, signature disposition, findings and immutable `previewReceipt`. Defaults block candidate emission. Explicit `review-only` and `preserve-unvalidated` permit a separate review candidate, while continuing to require native AOP acceptance. There is no `zero` or `regenerate` policy.
6. Call `studio_safety_io_apply` with `request:{preview:<complete unchanged edit request>,expectedReviewSha256,previewReceiptId,previewReceiptSha256}`. Apply rechecks the source/revision, profile files, raw configuration and before-values, then creates a separate managed project, exportable L5X candidate and immutable JSON receipt. Only changed byte-literal spans are patched. Original encoding/BOM, CRLF/CDATA, unselected bytes/bits, E-stop inputs, SNN, network/timing, I/O data/forces, libraries and signature fields stay exact. Save artifacts with `studio_save_artifact` to unused absolute paths.
7. After an uncertain response, inspect `studio_safety_io_status` using `previewReceiptId` and `expectedSha256` equal to its receipt hash. Completed exact applies return the existing result; indeterminate attempts are not replayed. Follow the separately authorized native handoff above on a trusted baseline; merely importing this byte candidate is not evidence that its SCID was regenerated.

The supported layout is validated against the exact 55 physical members and 24 bit fields before offsets are derived. Profiles are limited to 1 MiB each, 20000 XML elements and 32 levels; project XML retains the 64 MiB / 1000000-element / 64-level preservation limits. Configuration text is limited to 16 KiB, exactly 90 byte literals, 8 input edits and 4 test-output edits; reviews are limited to 4 MiB. Synthetic tests verify the example changes affect only offsets 22, 23, 66 and 72 for the supported layout, with nonzero signature values, input 4/5 SafetyPulseTest on T0/T1, delays, pad bytes and unselected project data retained. These are offline preservation checks, not native module or safety acceptance.
