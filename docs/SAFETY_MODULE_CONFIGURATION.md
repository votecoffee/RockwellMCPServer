# Safety module configuration and SCID

RockwellMCPServer does **not** currently provide a native AOP channel-configuration editor or a supported module-SCID recalculation operation. A decoded configuration-byte edit is an **unvalidated review candidate**. Retaining the previous configuration signature does not establish that the changed payload is native-valid.

## Current support

| Route | Supported boundary |
|---|---|
| Module connection timing review | Changes existing RPI, network-delay multiplier and reaction-time attributes in a separate review copy. Preserves configuration payloads and signatures; performs no AOP or SCID recalculation. |
| Installed AOP/EDS parameter decoding | Can establish the meaning of a field for an exact profile/module revision. Does not establish how to regenerate its signature. |
| Native Logix Designer module properties | Rockwell documents automatic configuration-signature generation when configuring a safety I/O device through Logix Designer. This is a user-operated native handoff, not an automated service operation. |
| SDK import/build/save/export | Available for separately authorized offline project work. Completion does not prove that a modified safety-module payload caused AOP validation or SCID recalculation. |
| SDK safety-signature methods | Operate on the controller/project safety signature. They are not a module-configuration SCID generator. |

The SDK statement is scoped to the repository's frozen `RockwellAutomation.LogixDesigner.LogixProject.CSClient` assembly **2.1.974.0**: 67 signatures, 63 unique method names. That surface has no dedicated AOP parameter-edit or module-SCID recalculation method. A newer vendor package requires a fresh API review. See [SDK support](SDK.md) and the [frozen surface](../src/Studio5000.Core/SdkSurface.json).

## Payload, signature and safety-task signature are different evidence

For an export containing `ConfigCRC`, `ConfigTime` and `ConfigDate`, retain the complete tuple with its module configuration and exact source bytes. Rockwell describes a device configuration signature as an ID, date and time used to identify the configuration. This is separate from the controller's safety-task signature and from this application's SHA-256 file/review hashes. [Safety I/O Device Signature, 1756-UM543](https://www.rockwellautomation.com/en-us/docs/technical/logix5000/_online/1756-um543/controllogix-5580-and-guardlogix-5580-controllers-/safety-i-o-devices/safety-i-o-device-signature.html).

For example, even when an exact installed 1734-OBV2S AOP/EDS identifies a selected mode field's value `0` as `Not Used`, that establishes the field's interpretation only. It does not establish a CRC input domain, timestamp serialization, signature-generation algorithm or import-triggered recalculation. Do not generalize offsets or enums to another series, firmware or profile revision.

This integration has not established a documented SCID regeneration rule for hand-edited 1734-OBV2S L5X configuration data. The general import/export manual describes module `ConfigTag`/`ConfigData`; that is not a promise that importing changed bytes regenerates a consistent signature. See [1756-RM014D, Define a module component](https://literature.rockwellautomation.com/idc/groups/literature/documents/rm/1756-rm014_-en-p.pdf).

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
