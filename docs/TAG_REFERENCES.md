# PLC tag where-used

Case-insensitive native status keywords (`S:FS`, `S:N`, `S:Z`, `S:V`, `S:C`, `S:MINOR`) and module roots (`Device:I`, `Device:O`, `Device:C`, `Local:1:I`) are recognized as complete lexical operands. They do not become references to plain user tags named `S`, `FS`, `Device`, `Local` or member `Data`. Dynamic native I/O subscripts still expose separate index-expression references. Simple user-tag query selectors remain unchanged. Unknown qualifiers and malformed native selectors retain diagnostics. This is syntax coverage, not status evaluation or module/member binding. [Status keyword documentation](https://www.rockwellautomation.com/en-ua/docs/studio-5000-logix-designer/37-00/contents-ditamap/math-status-flags.html)

The shared service finds **lexical candidates** for an exact saved controller/program base tag in program RLL and Structured Text source, plus direct `AliasFor` declarations. It preserves occurrences, source selectors, UTF-16 character offsets, rung/line numbers and bounded source snippets. It never connects to a controller or modifies a source/revision.

## REST and MCP

Use `POST /api/projects/{id}/tag-references`:

```json
{
  "expectedSha256": "CURRENT_LOGIX_SHA256",
  "tagName": "Input",
  "programName": "ExampleProgram",
  "query": null,
  "offset": 0,
  "limit": 100
}
```

Omit/null `programName` for a controller tag. The exact base declaration must exist in the selected snapshot; a missing declaration returns `reference_tag_missing`. Names compare case-insensitively, and responses return the declaration's spelling. Every request requires its current snapshot hash; stale revisions return `stale_revision`.

Discover/describe the `logix` operation `studio_tag_references` through the stable extension bridge and invoke it with `studio_extensions_read`. Arguments are `{ "projectId": "...", "request": { ...the body above... } }`. The fixed MCP tool list stays unchanged.

`query` filters operand text, source program/owner, kind, role or status. `total` counts matching occurrences before paging. Resume with `nextOffset` and the identical query/hash. `scannedCandidates` counts the streamed lexical operands, including unrelated names; only the requested matching page is retained. Each result includes `sourceXPath`, `start`, `length`, `operand`, `snippet`, `snippetStart` and `snippetTruncated`. Offsets refer to the selected text element's value, or the alias attribute value. Positional XPath selectors distinguish duplicate rung/line numbers. `routineXPath` and the zero-based `rungIndex` allow the matching RLL rung to be opened in the ladder viewer.

## Desktop

Open **PLC where used…** from the main window and select a saved Logix snapshot, declaration scope and base tag. **Where used…** in the tag/UDT browser opens the selected tag's base declaration, including when a member/array cell is selected. **Where used in PLC…** in the ME/SE resolver opens the base tag of a matched PLC reference. Both entry points reject changed snapshots before opening; the where-used view keeps the opening hash until an explicit refresh.

Use **Find references**, the filter and Previous/Next to browse candidates. Select a result for its exact source selector, offsets, snippet, hash and diagnostics. **Open selected ladder rung…** opens RLL at the zero-based rung position, preserving the report's hash even when rung numbers are duplicated. ST and alias source remains in the details pane. Refreshing snapshots explicitly rebinds the selection to current saved metadata; no controller access occurs.

## Coverage

Local tag declarations shadow controller tags. An explicit `Program:Name.Tag` candidate selects that program scope. Member names are part of their base address, so a member named `Input` in `Record.Input` does not become a reference to a separate tag named `Input`.

The scanner skips quoted strings (including doubled quotes and dollar escapes), numeric/typed literals, call names and comments. It carries block-comment state across ST XML lines and recognizes `//`, `(* ... *)` and `/* ... */`, consistent with [Rockwell's ST comment syntax](https://www.rockwellautomation.com/en-il/docs/studio-5000-logix-designer/38-00/contents-ditamap/instruction-set/structured-text-attributes/st-components--comments.html). Unterminated comments/strings produce diagnostics and mask the remainder of that source. Comments stored separately in rung descriptions are not scanned.

`Samples[Index + 1]` produces a `dynamic_address_candidate` for `Samples` and a separate `index_expression` occurrence for `Index`. Static addresses have status `candidate`; malformed array selectors have `syntax_unresolved`. This scan does not validate members, bounds or expression grammar. It identifies spelling and declaration scope, rather than compiler-bound semantics. Labels, routine names and symbolic instruction operands can share tag spelling and remain candidates for review.

Alias targets are direct occurrences only: searching the base tag does not expand every use of its aliases. AOI-local routines, FBD/SFC and unsupported qualified/module addresses are skipped with bounded diagnostics. `scannedSources` counts scanned rung text, ST line and alias sources; `skippedSources` counts unsupported/missing routine sources. Partial exports and protected content can omit references. An empty report does **not** prove a tag is unused or safe to remove. No read/write classification, native where-used equivalence or runtime behavior is claimed.

## Limits and validation

Limits: 64 MiB per saved export, 1000000 XML elements, 64 XML nesting levels, 200 results per page, 256 query/name characters, offset 0..16777216, 64 KiB per rung, 1 MiB per ST routine scan, 16 MiB scanned text, 4096 address characters, 16 array-expression levels, 32 comment levels, 50 diagnostics and a 4 MiB encoded response. Unrelated lexical candidates and total matches have no separate source-wide count ceiling: the text budget bounds scanning and the requested page bounds stored rows. Oversized scans return `tag_reference_limit`; invalid paging returns `invalid_tag_reference_query`; oversized replies return `tag_reference_page_too_large` and can be retried as a read with a smaller page. XML analysis limits remain enforced before indexing.

For reviewed whole-declaration removal, use the separate [dependency-closed cleanup workflow](UNUSED_CLEANUP.md). An empty where-used page alone is insufficient evidence for deletion.

[ReferencesDemo.L5X](../examples/ReferencesDemo.L5X) is a generated fixture with local/controller shadowing, RLL/ST, indirect indices and an alias. Unit checks cover source selectors, literals/comments, unsupported syntax, scope and resource bounds. Integration checks exercise the same operation through authenticated REST and the unchanged MCP bridge. These checks establish local scan behavior, not native Studio acceptance.
