# Graphite

**Read, mark up, and edit PDFs — a fast, native Windows app.**

Graphite is a WPF (.NET 10) PDF viewer and editor with an original liquid-glass design:
a floating command bar, a pill-shaped page bar, and translucent panels over the
Windows 11 Mica backdrop. No Electron, no browser engine — PDFium rendering and a
pure managed core keep it quick and small.

![Graphite](src/Graphite.App/Assets/Graphite-256.png)

## Download

Grab the latest `Graphite-vX.Y.Z-win-x64.zip` from
[Releases](https://github.com/nazem24/graphite/releases), unzip, and run `Graphite.exe`.
It's a single self-contained executable — no installer, no .NET runtime needed.

Graphite **updates itself**: it checks for new releases at startup (quietly — it only
speaks up when there's something new) and from *File → Check for updates…*. Accepting
an update downloads the package, checks it against the SHA-256 published with the release
(a damaged download is never installed), swaps the files and restarts the app. The swap is
transactional: everything it replaces is backed up first and restored if anything goes wrong,
and it only ever removes files a previous Graphite installed (listed in `Graphite.files`).

## Features

**Viewing** — PDFium-based rendering (fast, high-fidelity), eased smooth scrolling,
smooth zoom (buttons, Ctrl+wheel, fit-width, click the zoom % to reset to 100%),
continuous / single-page / two-page spread layouts, page thumbnails, outline
navigation, full-text search with per-page highlights, and multi-tab support —
each tab remembers its scroll position.

**Library start screen** — the Home tab is a mirror of one main folder (OneDrive by default): browse its subfolders like a file manager, search across them (Ctrl+F), pin files, and pick up where you stopped with a *Continue reading* card and per-file progress. Files that are online-only in OneDrive are marked with a cloud and never downloaded just to be listed. Choose the folder once from *Choose your main folder*; open documents stay open in their tabs behind Home.

**Reading** — fullscreen mode (F11, Esc to leave), dark pages (inverted page colors
for night reading, moon button in the page bar), and reading history: Alt+Left /
Alt+Right walks back and forward through your jumps.

**Pen & markup** — highlight, underline and strikethrough that snap to words, a
freehand marker, and a pressure-friendly **pen tuned for styluses**: full-rate pen
sampling (no coalesced points thrown away) with a jitter filter and an
overshoot-free curve fit, so handwriting follows the pen instead of lagging and
snapping. Plus rectangles, ellipses, arrows, text boxes (click and type right on
the page; double-click later for font, size and colors), sticky notes, and an
eraser that splits strokes exactly where you drag.

**Forms** — interactive PDF forms can be filled in right on the page: text fields (single
and multi-line, comb and password fields), check boxes, radio buttons, drop-downs and list
boxes. Fillable fields are tinted with the Select tool; values are written with proper
appearance streams, so the filled form looks right in every viewer, and every change is
undoable. Combine it with the signature tool to fill and sign.

**Signature** — draw your signature once and stamp it onto any page; it's kept
between sessions. Click the signature tool again (or right-click it) to redraw.

**Comments** — every annotation carries a comment with threaded replies. The Markup
panel lists everything in the document; click a card to jump to it. Annotations are
written as standard PDF annotations, so they open in any other viewer. Undo/redo
(Ctrl+Z / Ctrl+Y) covers annotations and page operations alike.

**Page editing** — insert (blank or from another PDF), delete, reorder, rotate and
extract pages; merge PDFs; split into single-page files. Right-click a thumbnail
for per-page operations. Annotations travel with their pages through every operation.

**Content editing** — an Edit Text tool (drag over a region, type the replacement —
the region is covered and re-typeset, how most PDF editors work under the hood since
PDF has no reflowable text) and an Insert Image tool; images can also be pasted
straight from the clipboard (Ctrl+V).

**Notes from markup** — *Export → Highlights & comments to notes* collects every highlighted
passage (quoted, in its highlight colour), comment, reply and text box into a new document of
the built-in editor next to the PDF, page by page — a ready-made summary to keep studying from.

**Command palette** — Ctrl+K opens a searchable palette with every command in the
app; type a few letters, Enter runs it.

**Conversion** — export to Word (.docx, reconstructed paragraphs), Excel (.xlsx, one
sheet per page with inferred columns), PowerPoint (.pptx, one slide per page) and
PNG/JPEG/WebP images. Opening a Word/Excel/PowerPoint file converts it to a PDF
saved next to the original and opens that (uses the installed Microsoft Office
via COM; requires Office).

**OCR** — Tesseract-based recognition for scanned pages. Recognized pages become
searchable and selectable immediately, and an invisible text layer is baked into the
PDF so it stays searchable after saving, in any viewer.

**Security & info** — password-protected PDFs open with a prompt and stay protected
when you save; any document can be saved as an encrypted copy. A properties dialog
shows title, author, dates, producer, PDF version, page size and file size.

**Safety net** — while a PDF has unsaved changes, a recovery copy is kept in
`%LOCALAPPDATA%\Graphite\Recovery` (re-encrypted for password-protected files); after a crash
or power loss Graphite offers the changes back at the next start. If an open file is changed
by another program (OneDrive sync, another editor), an unmodified tab reloads by itself and a
modified one asks whether to reload or keep your version — saving never silently overwrites
someone else's changes.

**Platform** — single instance (opening another PDF from Explorer adds a tab to the running
window), dark/light themes (persisted), Windows 11 Mica backdrop, per-monitor
DPI awareness, drag-and-drop, recent files, OneDrive quick-open, printing at 300 dpi
with annotations included, and a script to register the .pdf file association.

## Building

Requirements: **Windows 10/11** and the **.NET 10 SDK** (or Visual Studio 2026 with
the ".NET desktop development" workload).

```
dotnet build Graphite.sln -c Release
dotnet run --project src/Graphite.App
```

Or open `Graphite.sln` in Visual Studio and press F5. NuGet restores everything
(PDFtoImage/PDFium, PdfSharp, PdfPig, DocumentFormat.OpenXml, ClosedXML, Tesseract,
CommunityToolkit.Mvvm).

### Tests

```
dotnet test src/Graphite.Tests
```

Unit tests cover the Core engine: page-range parsing, text layout reconstruction,
annotation codec round-trips (through real PDFs), color/geometry helpers, search word
mapping, and encryption round-trips. CI builds and tests on every push and PR;
tagging `v*` publishes a release zip automatically.

### Optional setup

- **OCR data** — `powershell -File tools\get-tessdata.ps1` downloads English and German
  (`eng`, `deu`) into `src/Graphite.App/tessdata` (copied next to the exe on build). All
  languages found there are used together. Pass `-Languages eng,deu,fra,...` for more.
- **File association** — after building:
  `powershell -File tools\register-file-association.ps1 -ExePath <path>\Graphite.exe`,
  then choose Graphite for `.pdf` in Settings → Default apps (Windows requires that
  final choice to be made by the user).
- **App icon** — `python tools\gen_icon.py` regenerates `Assets\Graphite.ico` /
  `Graphite-256.png` (the pencil-and-"G" artwork is generated, not a stock asset).

## Architecture

```
Graphite.sln
├─ src/Graphite.Core     ── UI-independent engine (net10.0)
│   ├─ Rendering/        PDFium rasterizer (PDFtoImage), thread-safe
│   ├─ Text/             PdfPig text index: word geometry, search, outline, selection
│   ├─ Pdf/              PdfSharp page surgery: merge, split, extract, rotate, reorder;
│   │                    PageGeometry (display ↔ PDF space: /Rotate, CropBox)
│   ├─ Annotations/      model + codec that reads/writes real PDF annotations,
│   │                    appearance streams for every annotation type
│   ├─ Forms/            AcroForm fields: read, fill, appearance streams
│   ├─ Editing/          cover-and-replace text edits, image placement
│   ├─ Ocr/              Tesseract engine + invisible-text-layer writer
│   └─ Export/           docx / xlsx / pptx / image exporters, Office→PDF via COM
└─ src/Graphite.App      ── WPF shell (net10.0-windows)
    ├─ Themes/           light/dark palettes, styles, original icon geometry
    ├─ ViewModels/       Main / Document / Page / Annotation (CommunityToolkit.Mvvm)
    ├─ Controls/         AnnotationLayer (per-page overlay), FormLayer (form filling),
    │                    StrokeSmoothing (pen input)
    ├─ Services/         theme/settings, signature store, printing, self-updater,
    │                    single instance, crash recovery, markup → notes
    ├─ Views/            MainWindow + dialogs (input, edit-text, password, signature, properties)
    └─ Interop/          Mica backdrop + dark title bar (DWM)

src/Graphite.Tests       ── xUnit tests for the Core engine
```

Design decisions worth knowing:

- **Operations are pure** (`bytes in → bytes out`). Before any structural operation the
  current annotations are baked into the PDF and re-read afterwards — so they stay glued
  to their pages through deletes, moves and merges.
- **Rendering is decoupled from layout.** Pages lay out at `size × zoom` immediately;
  bitmaps re-render ~1.5× oversampled in the background (debounced on zoom), so zooming
  feels instant.
- **The viewer virtualizes.** Pages render lazily as they scroll into view; thumbnails
  likewise.
- **One coordinate space.** Everything above the Core works in the page *as displayed*
  (CropBox, `/Rotate` applied, top-left origin, points) — what PDFium renders and PdfPig
  reports. `PageGeometry` converts to PDF user space at the file boundary, so markup, form
  fields and content edits land correctly on rotated and cropped pages.
- **Pen input keeps every sample.** Stylus hardware reports faster than the UI
  dispatches; Graphite drains the coalesced point queue instead of dropping it, then
  fits a midpoint-quadratic curve that can't overshoot into loops.

## Design language

Graphite pairs its warm-gray monochrome identity with a liquid-glass treatment that
sits naturally on Windows 11: a floating glass command bar and a pill-shaped page bar
hover over the document, side panels are translucent gradient glass with a luminous
top edge and soft radii, and tabs are pills. Buttons animate on hover and compress on
press. The system accent color appears sparingly — checked tools, focus rings, the
unsaved-changes dot — while functional color stays reserved for the document itself.
Icons are original 1.3px hairline stroke geometry (no icon font), drawn to read
clearly at 16px; the app icon is a generated artwork: a pencil resting on the "G"
stroke it just drew.

## Known limitations

- Text editing is cover-and-replace (see above) — original fonts are not re-embedded.
- Word/Excel export reconstructs layout heuristically from text geometry; complex
  layouts and tables degrade gracefully but imperfectly.
- Creating PDFs *from* Office files requires Microsoft Office to be installed.
- Text-box and form-field appearances use the standard Helvetica faces (no font embedding),
  so characters outside Western European (WinAnsi) text show as "?" in those appearances;
  the form values themselves keep full Unicode.
- The self-updater needs write access to the install folder; if it can't swap the
  files it falls back to opening the release page.
