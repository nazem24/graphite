# Graphite — Codebase Analysis & Improvement Suggestions

Scope: full read of all 36 source files (~5,800 lines of C#) in `src/Graphite.Core` and
`src/Graphite.App`, plus solution files, CI workflow, tools scripts and README.

Overall verdict: this is a well-crafted codebase. Layering (UI-independent Core / WPF shell),
the "operations are pure bytes-in → bytes-out" convention, lazy rendering with eviction,
frozen drawing resources, and the undo memory budget are all thoughtful, and the comments
explain *why*, not just *what*. The suggestions below are ordered by impact: real bugs first,
then security/data-safety, performance, architecture, and engineering hygiene.

---

## 1. Bugs and correctness issues

### 1.1 Eraser marks the document dirty even when it erases nothing — `AnnotationLayer.cs:568-571`
`EraseAt` runs on every mouse-move while the eraser is down. When no stroke was touched,
`toRemove.Count == 0` and it calls `_doc.NotifyAnnotationChanged()` unconditionally —
which sets `IsDirty = true`. Dragging the eraser over blank space (or even just hovering
with the button held) makes the document "modified" and triggers the save-on-close prompt.
Fix: track whether any annotation actually changed and only notify then:

```csharp
bool anyChanged = changed || toRemove.Count > 0; // accumulate across the loop
if (!anyChanged) return;
...
_doc.NotifyAnnotationChanged();
```

### 1.2 Comment editing pushes a full undo snapshot per keystroke — `AnnotationViewModel.cs:54-66`
The `Contents` setter calls `Doc.PushUndo()` on every change. `Contents` is bound to a
text box, so typing a 40-character comment pushes 40 undo entries (each cloning every
annotation in the document) and flushes the 30-step undo history. Undo then restores one
keystroke at a time. Fix: coalesce — push undo once when the edit *starts* (focus /
first change after load), or debounce (push at most one snapshot per N seconds per
annotation), or snapshot on `LostFocus` instead of per keystroke.

### 1.3 Saving overwrites the original file non-atomically — `DocumentViewModel.cs:481-507`
`File.WriteAllBytesAsync(path, output)` writes directly over the user's PDF. A crash or
power loss mid-write leaves a truncated, unrecoverable file — the worst possible outcome
for a document editor. Fix: write to `path + ".tmp"` (same volume), then
`File.Move(tmp, path, overwrite: true)`. Optionally keep one `.bak` of the previous version.

### 1.4 Saving a password-protected PDF silently strips its protection — `MainViewModel.cs:108-115`
Protected files are decrypted in memory at open; `SaveAsync` then writes the *decrypted*
bytes back over the original file. The user is never told their document is no longer
protected. Fix: remember that the document was opened from an encrypted file and either
re-encrypt on save with the same password, or warn on first save ("Saving will remove
password protection — continue?").

### 1.5 FreeText `/DA` references font resources that don't exist in the PDF — `AnnotationCodec.cs:328-337`
The default-appearance string is written as `/Helv 12 Tf …` (etc.), but `/Helv` is a
*resource name*, and no matching font resource is added to the page's `/Resources` or the
document AcroForm `/DR`. Viewers that honor `/DA` strictly (rather than regenerating
appearances heuristically) can render nothing or fall back unpredictably. Fix: add a
Helvetica base-14 font dictionary to the document's AcroForm default resources under the
names you reference (`/Helv`, `/HeBo`, `/HeOb`, `/HeBO`), or embed a small font and
reference that. Worth testing the output in Acrobat and Firefox's pdf.js, which differ here.

### 1.6 Corrupt PDFs are misreported as password-protected — `PdfSecurity.cs:11-21`
`IsPasswordProtected` returns `true` for any `PdfReaderException`, which is also what a
damaged file throws. The user then gets a password prompt that can never succeed.
Fix: distinguish "needs password" from "broken" (PdfSharp throws
`PdfReaderException`/`InvalidPasswordException` distinctly in recent versions — check the
exception type/message), and surface "file is corrupted" separately.

### 1.7 `Annotation.ParseColor` throws on malformed input — `Annotation.cs:105-113`
`Convert.ToInt32(hex[0..2], 16)` throws `FormatException` on a bad string, and indexing
throws on short strings. It's called during annotation save (`AnnotationCodec.BuildDict`)
and content editing, so one bad color string (e.g. from a hand-edited settings file or a
future color picker) aborts the whole save. Fix: `TryParse`-style guard with a gray/black
fallback, mirroring the tolerant `ParseColor` already in `AnnotationLayer`.

### 1.8 Annotation dates are written without a timezone — `AnnotationCodec.cs:412`
`FormatDate` emits `D:yyyyMMddHHmmss` with no UTC offset, so the timestamp is ambiguous to
other viewers (PDF dates should be `D:yyyyMMddHHmmss+02'00'` etc.). Also `ParseDate`
silently substitutes `DateTime.Now` on parse failure, which rewrites the original
modification time of annotations made in other apps. Minor, but cheap to fix.

### 1.9 `PushUndo` runs before operations that may fail — `DocumentViewModel.cs:424-429`
`ApplyOperationAsync` pushes an undo snapshot and *then* runs the operation. If the
operation throws, the undo stack holds a snapshot identical to the current state, so the
next Ctrl+Z appears to do nothing. Same pattern in `AnnotationLayer` when a dialog is
cancelled after `PushUndo` (e.g. image-move corner hit followed by no drag). Harmless but
confusing; consider validating first or popping the snapshot on failure.

### 1.10 README describes a Mica backdrop the code deliberately disables
`README.md` (intro + "Design language") sells the "Mica backdrop with glass panels", but
`Interop/Backdrop.cs` explicitly sets `BACKDROP_NONE` with a long comment explaining Mica
was abandoned for contrast reasons. The docs and the code tell opposite stories — update
the README (or restore Mica with a solid fallback).

---

## 2. Security

- **Weak encryption algorithm** — `PdfSecurity.Encrypt` uses `SetEncryptionToV2With128Bits()`,
  which is RC4-128, deprecated and considered weak. Check whether your PdfSharp 6.1 version
  offers AES-256 (`V5`); if it does, switch. Also consider *not* setting the owner password
  equal to the user password — as written, anyone who can open the file has full owner
  permissions, so permission flags are meaningless.
- **Decrypted content lingers** — decrypted bytes live in `_bytes`, the renderer, the text
  index, and every undo snapshot for the session. That's inherent to the design, but combined
  with 1.4 it means protection is easily lost without the user noticing.
- **Office COM automation** — `OfficeToPdf` opens arbitrary documents in Word/Excel/PowerPoint
  with macros not explicitly disabled. At minimum set `AutomationSecurity` to force-disable
  macros before `Open`, since a malicious .doc opened in Graphite would otherwise run its
  auto-macros inside Office.

---

## 3. Performance

### 3.1 OCR rewrites the entire PDF once per recognized page — `DocumentViewModel.cs:614-633`
`working = SearchablePdfWriter.AddTextLayer(working, p, words)` runs inside the page loop:
a 200-page scanned document is parsed and re-serialized 200 times (O(n²)). Fix: collect all
pages' word boxes first, then open the document once and append every page's text layer in a
single pass (requires changing `AddTextLayer` to accept a per-page map, or exposing an
`AddTextLayers(byte[], IReadOnlyDictionary<int, IReadOnlyList<WordBox>>)` overload).
Same pattern, lower stakes, in `ApplyOperationAsync` baking placed images one full rewrite
at a time.

### 3.2 Search has no cancellation and no debounce — `DocumentViewModel.cs:546-568`
`TextIndex.Search` accepts a `CancellationToken` but `RunSearchAsync` never passes one.
Pressing Enter twice (or searching again while a long search runs) executes overlapping
searches whose results race into `SearchResults`. Fix: keep a `CancellationTokenSource` per
document, cancel the previous search at the top of `RunSearchAsync`, and pass the token
through. Debouncing the search box (300 ms) would make search-as-you-type essentially free
on top of this.

### 3.3 PDFium re-parses the document on every render — `PdfRenderer.cs`
`Conversion.ToImage(_pdf, …)` opens the whole PDF from bytes for each page render and each
thumbnail. For large documents this is likely the dominant render cost. Investigate keeping
one loaded PDFium document per file (PDFtoImage exposes lower-level APIs) behind the same
lock, or at least measure it before optimizing.

### 3.4 Smaller wins
- `SearchablePdfWriter` creates a new `XFont` per word — cache fonts by size (a scanned page
  yields thousands of words).
- `AnnotationLayer.OnRender` allocates a `Point[]` and a `StreamGeometry` per stroke per
  frame, and a `FormattedText` per FreeText per frame. During ink drags this runs per
  mouse-move; cache geometry per annotation and rebuild only on change/zoom.
- `InvertBgra` is a scalar byte loop over multi-MB buffers — `System.Numerics.Vector<byte>`
  or `Parallel` would speed up dark-mode toggles measurably.
- `PowerPointExporter` stores PNGs with `CompressionLevel.Fastest`; PNG data is already
  compressed, so `NoCompression` is strictly faster for the same size.
- Two full copies of every document are held per tab (`DocumentViewModel._bytes` and
  `PdfRenderer._pdf`), plus undo snapshots. The renderer could borrow the bytes instead of
  copying (it already just stores the reference — the fix is to pass the same array rather
  than the original file bytes twice; today `Load` returns the input, so this is already
  shared *if* the caller passes the same array — verify at the call sites).

---

## 4. Architecture

- **`DocumentViewModel` (644 lines), `MainViewModel` (610), `AnnotationLayer` (969) are
  doing too much.** Natural seams:
  - `DocumentViewModel` → extract an `UndoService` (snapshot stack + budget) and a
    `SearchController` (query state, cancellation, results).
  - `MainViewModel` → extract file-open/close orchestration and export commands into
    services; the palette builder could be data-driven (a command registry the toolbar and
    palette both consume) instead of a hand-maintained list that can drift from the UI.
  - `AnnotationLayer` → split into an input/gesture controller (hit-testing, drag state
    machine) and a renderer (all the `Draw*` methods + frozen resource cache). The drag
    state is currently spread across 10 fields and would read much more clearly as a small
    state machine.
- **No interfaces around Core services.** `IPdfRenderer` / `ITextIndex` would let you unit
  test view-model logic without PDFium/PdfPig, and would make the threading contract
  (renderer = locked, index = locked) explicit.
- **Threading hazards worth a comment or a guard**: `RestoreSnapshotAsync` and
  `ApplyOperationAsync` mutate `Renderer`/`Index` inside `Task.Run` while the UI thread may
  simultaneously read `Renderer.PageSizes` (layout) or call `Index.WordsInRect` (mouse
  move). The locks in `TextIndex` cover it, but `PdfRenderer.PageSizes` is replaced without
  a lock. `RunOcrAsync` also sets the `BusyText` observable property from a background
  thread — works for scalar WPF bindings today, but it's fragile; marshal via Dispatcher.
- **`ThemeService` is a static god-object** mixing settings persistence, theme application,
  registry access, and the signature store. Splitting `SettingsStore` (JSON load/save) from
  `ThemeService` (resources) and `SignatureStore` would make each testable and would remove
  the fragile `MergedDictionaries[0]` index assumption (find the colors dictionary by its
  `Source` instead).
- **Single-instance behavior**: opening a second PDF via file association launches a second
  process. Consider a single-instance app with inter-process forwarding of file paths —
  expected behavior for a document viewer.

---

## 5. Testing & engineering hygiene

- **There are no tests.** The Core project is unusually testable (pure bytes-in/bytes-out):
  - `PageOperations.ParsePageRanges` — parsing, ranges, errors, duplicates.
  - `TextLayout` line/paragraph/cell clustering on synthetic word boxes.
  - `AnnotationCodec` write→read round-trips (including freehand highlight marker,
    replies, quads, FreeText formatting keys).
  - `PageText.WordsInRange` binary search against a brute-force oracle.
  - `RectD` geometry, `Annotation.ParseColor`/`ToHex` round-trip, invert LUT math.
  Add a `Graphite.Tests` xUnit project; the pure design means no UI harness is needed.
- **CI only runs on release tags.** Add a PR workflow: `dotnet build -c Release` +
  `dotnet test` + (optionally) `dotnet format --verify-no-changes`. Release workflow could
  also stamp the assembly version from the tag and sign the exe/zip.
- **.NET 8 goes out of support in November 2026.** Plan the move to .NET 10 (LTS) — for
  WPF this is normally a one-line `TargetFramework` change plus package updates.
- **Dependency updates**: PdfPig 0.1.9 and PdfSharp 6.1.1 have newer releases; add
  Dependabot/Renovate so this doesn't go stale.
- **Silent `catch { }` blocks** (`PageViewModel.EnsureRenderedAsync`, `EnsureThumbnailAsync`,
  several in `MainViewModel`) hide real failures. At minimum log to
  `Debug.WriteLine`/a log file so rendering regressions are diagnosable.
- **Accessibility pass**: dialogs and the custom caption buttons would benefit from
  `AutomationProperties.Name`, and the command palette already gives good keyboard coverage —
  extending it to annotation tools would help keyboard-only users.

---

## 6. Quick wins (small effort, immediate value)

1. Fix the eraser dirty-flag (1.1) — three lines.
2. Coalesce comment-edit undo (1.2).
3. Atomic save (1.3) — ~5 lines.
4. Update README's Mica claims (1.10).
5. Pass a `CancellationToken` into search (3.2).
6. Cache `XFont` instances in `SearchablePdfWriter` (3.4).
7. Guard `Annotation.ParseColor` (1.7).
8. Timezone in annotation dates (1.8).
9. `NoCompression` for PNGs in the pptx exporter (3.4).
10. Add the PR build workflow (§5).
