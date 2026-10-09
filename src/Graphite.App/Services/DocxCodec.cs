using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using Graphite.Core.Library;

namespace Graphite.App.Services;

/// <summary>
/// Reads and writes the editor's documents as ordinary Word (.docx) files. Only the part of
/// WordprocessingML the editor can produce is understood: paragraphs with styles, alignment,
/// spacing and character formatting, bullet and numbered lists (nested), tables, hyperlinks and
/// inline pictures. The package is written by hand (zip + LINQ to XML) so a file is always a valid
/// .docx that Word, LibreOffice and Graphite's own "convert to PDF" can open.
/// </summary>
public static class DocxCodec
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace PIC = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";

    private const string RelStyles = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";
    private const string RelNumbering = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering";
    private const string RelImage = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
    private const string RelHyperlink = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";
    private const string RelOfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string RelCoreProps = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";
    private const string RelExtendedProps = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties";

    // ------------------------------------------------------------- creating documents

    /// <summary>Set the page-wide look: font, size, black ink on white paper, page size and margins.</summary>
    public static void Configure(FlowDocument doc, PageSettings page)
    {
        doc.FontFamily = new FontFamily(DocStyles.DefaultFont);
        doc.FontSize = Units.PtToDip(DocStyles.DefaultSizePt);
        doc.Foreground = Brushes.Black;
        doc.Background = Brushes.White;
        doc.PageWidth = page.WidthDip;
        doc.PagePadding = new Thickness(page.MarginDip);
        doc.TextAlignment = TextAlignment.Left;
    }

    public static FlowDocument NewDocument(PageSettings page)
    {
        var doc = new FlowDocument();
        Configure(doc, page);
        doc.Blocks.Add(DocStyles.NewParagraph());
        return doc;
    }

    // ------------------------------------------------------------- saving

    /// <summary>Write <paramref name="doc"/> to <paramref name="path"/> (write-then-rename, so a crash
    /// never leaves a half-written file).</summary>
    public static void Save(string path, FlowDocument doc, PageSettings page, string title)
    {
        byte[] bytes = Build(doc, page, title);
        string tmp = path + ".graphite-tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        GraphiteDocx.Forget(path);
    }

    public static byte[] Build(FlowDocument doc, PageSettings page, string title)
    {
        var writer = new DocxWriter(page);
        XDocument document = writer.BuildDocument(doc);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddXml(zip, "[Content_Types].xml", writer.BuildContentTypes());
            AddXml(zip, "_rels/.rels", BuildRootRels());
            AddXml(zip, "docProps/core.xml", BuildCoreProps(title));
            AddXml(zip, "docProps/app.xml", BuildAppProps());
            AddXml(zip, "word/document.xml", document);
            AddXml(zip, "word/_rels/document.xml.rels", writer.BuildDocumentRels());
            AddXml(zip, "word/styles.xml", BuildStyles());
            AddXml(zip, "word/numbering.xml", writer.BuildNumbering());
            foreach (var (name, data) in writer.Media)
            {
                var entry = zip.CreateEntry("word/media/" + name, CompressionLevel.NoCompression);
                using var s = entry.Open();
                s.Write(data, 0, data.Length);
            }
        }
        return ms.ToArray();
    }

    private static void AddXml(ZipArchive zip, string name, XDocument xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        xml.Save(s, SaveOptions.DisableFormatting);
    }

    private static XDocument Doc(XElement root) => new(new XDeclaration("1.0", "UTF-8", "yes"), root);

    private static XDocument BuildRootRels() => Doc(new XElement(PkgRel + "Relationships",
        new XElement(PkgRel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", RelOfficeDocument), new XAttribute("Target", "word/document.xml")),
        new XElement(PkgRel + "Relationship", new XAttribute("Id", "rId2"), new XAttribute("Type", RelCoreProps), new XAttribute("Target", "docProps/core.xml")),
        new XElement(PkgRel + "Relationship", new XAttribute("Id", "rId3"), new XAttribute("Type", RelExtendedProps), new XAttribute("Target", "docProps/app.xml"))));

    private static XDocument BuildCoreProps(string title)
    {
        XNamespace cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace dcterms = "http://purl.org/dc/terms/";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        string now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return Doc(new XElement(cp + "coreProperties",
            new XAttribute(XNamespace.Xmlns + "cp", cp.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dc", dc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dcterms", dcterms.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
            new XElement(dc + "title", Clean(title)),
            new XElement(dc + "creator", GraphiteDocx.ApplicationName),
            new XElement(dcterms + "created", new XAttribute(xsi + "type", "dcterms:W3CDTF"), now),
            new XElement(dcterms + "modified", new XAttribute(xsi + "type", "dcterms:W3CDTF"), now)));
    }

    // The exact text "<Application>Graphite</Application>" is how GraphiteDocx recognizes our files.
    private static XDocument BuildAppProps()
    {
        XNamespace ep = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
        return Doc(new XElement(ep + "Properties",
            new XElement(ep + "Application", GraphiteDocx.ApplicationName),
            new XElement(ep + "AppVersion", "16.0000")));
    }

    private static XDocument BuildStyles()
    {
        XElement ParaStyle(string id, string name, double sizePt, int outline, double beforePt, double afterPt) =>
            new(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "styleId", id),
                new XElement(W + "name", new XAttribute(W + "val", name)),
                new XElement(W + "basedOn", new XAttribute(W + "val", "Normal")),
                new XElement(W + "next", new XAttribute(W + "val", "Normal")),
                new XElement(W + "uiPriority", new XAttribute(W + "val", outline < 0 ? "10" : "9")),
                new XElement(W + "qFormat"),
                new XElement(W + "pPr",
                    new XElement(W + "keepNext"),
                    new XElement(W + "spacing",
                        new XAttribute(W + "before", (int)Math.Round(beforePt * 20)),
                        new XAttribute(W + "after", (int)Math.Round(afterPt * 20))),
                    outline >= 0 ? new XElement(W + "outlineLvl", new XAttribute(W + "val", outline)) : null),
                new XElement(W + "rPr",
                    new XElement(W + "b"), new XElement(W + "bCs"),
                    new XElement(W + "sz", new XAttribute(W + "val", (int)Math.Round(sizePt * 2))),
                    new XElement(W + "szCs", new XAttribute(W + "val", (int)Math.Round(sizePt * 2)))));

        return Doc(new XElement(W + "styles",
            new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
            new XElement(W + "docDefaults",
                new XElement(W + "rPrDefault", new XElement(W + "rPr",
                    new XElement(W + "rFonts",
                        new XAttribute(W + "ascii", DocStyles.DefaultFont), new XAttribute(W + "eastAsia", DocStyles.DefaultFont),
                        new XAttribute(W + "hAnsi", DocStyles.DefaultFont), new XAttribute(W + "cs", DocStyles.DefaultFont)),
                    new XElement(W + "sz", new XAttribute(W + "val", "22")),
                    new XElement(W + "szCs", new XAttribute(W + "val", "22")),
                    new XElement(W + "lang", new XAttribute(W + "val", "en-GB"), new XAttribute(W + "eastAsia", "en-US"), new XAttribute(W + "bidi", "ar-SA")))),
                new XElement(W + "pPrDefault", new XElement(W + "pPr",
                    new XElement(W + "spacing", new XAttribute(W + "after", "160"), new XAttribute(W + "line", "240"), new XAttribute(W + "lineRule", "auto"))))),
            new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "default", "1"), new XAttribute(W + "styleId", "Normal"),
                new XElement(W + "name", new XAttribute(W + "val", "Normal")), new XElement(W + "qFormat")),
            new XElement(W + "style", new XAttribute(W + "type", "character"), new XAttribute(W + "default", "1"), new XAttribute(W + "styleId", "DefaultParagraphFont"),
                new XElement(W + "name", new XAttribute(W + "val", "Default Paragraph Font")),
                new XElement(W + "uiPriority", new XAttribute(W + "val", "1")), new XElement(W + "semiHidden"), new XElement(W + "unhideWhenUsed")),
            new XElement(W + "style", new XAttribute(W + "type", "table"), new XAttribute(W + "default", "1"), new XAttribute(W + "styleId", "TableNormal"),
                new XElement(W + "name", new XAttribute(W + "val", "Normal Table")),
                new XElement(W + "uiPriority", new XAttribute(W + "val", "99")), new XElement(W + "semiHidden"), new XElement(W + "unhideWhenUsed"),
                new XElement(W + "tblPr",
                    new XElement(W + "tblInd", new XAttribute(W + "w", "0"), new XAttribute(W + "type", "dxa")),
                    new XElement(W + "tblCellMar",
                        new XElement(W + "top", new XAttribute(W + "w", "0"), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "left", new XAttribute(W + "w", "108"), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "bottom", new XAttribute(W + "w", "0"), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "right", new XAttribute(W + "w", "108"), new XAttribute(W + "type", "dxa"))))),
            new XElement(W + "style", new XAttribute(W + "type", "numbering"), new XAttribute(W + "default", "1"), new XAttribute(W + "styleId", "NoList"),
                new XElement(W + "name", new XAttribute(W + "val", "No List")),
                new XElement(W + "uiPriority", new XAttribute(W + "val", "99")), new XElement(W + "semiHidden"), new XElement(W + "unhideWhenUsed")),
            ParaStyle(DocStyles.Title, "Title", 26, -1, 0, 8),
            ParaStyle(DocStyles.Heading1, "heading 1", 20, 0, 18, 6),
            ParaStyle(DocStyles.Heading2, "heading 2", 16, 1, 14, 4),
            ParaStyle(DocStyles.Heading3, "heading 3", 13, 2, 10, 4),
            new XElement(W + "style", new XAttribute(W + "type", "character"), new XAttribute(W + "styleId", "Hyperlink"),
                new XElement(W + "name", new XAttribute(W + "val", "Hyperlink")),
                new XElement(W + "basedOn", new XAttribute(W + "val", "DefaultParagraphFont")),
                new XElement(W + "uiPriority", new XAttribute(W + "val", "99")), new XElement(W + "unhideWhenUsed"),
                new XElement(W + "rPr",
                    new XElement(W + "color", new XAttribute(W + "val", "0563C1")),
                    new XElement(W + "u", new XAttribute(W + "val", "single"))))));
    }

    // ------------------------------------------------------------- text helpers

    /// <summary>Drop characters XML cannot carry (control characters, lone surrogates).</summary>
    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(c).Append(s[i + 1]);
                i++;
            }
            else if (XmlConvert.IsXmlChar(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 6) return null;
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return null;
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color? SolidColor(Brush? brush) =>
        brush is SolidColorBrush { Color: var c } sb && sb.Opacity > 0 ? c : null;

    private static bool IsBullet(TextMarkerStyle style) =>
        style is TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square or TextMarkerStyle.Box or TextMarkerStyle.None;

    public static string ImageExtension(byte[] data)
    {
        if (data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "png";
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8) return "jpeg";
        if (data.Length > 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F') return "gif";
        if (data.Length > 2 && data[0] == 'B' && data[1] == 'M') return "bmp";
        return "";
    }

    public static BitmapImage LoadBitmap(byte[] bytes)
    {
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // ============================================================= writer

    private sealed class DocxWriter
    {
        private readonly PageSettings _page;
        private readonly List<(string Id, string Type, string Target, bool External)> _rels = new();
        private readonly List<XElement> _nums = new();
        private int _nextNumId = 2;           // 1 is the shared bullet list
        private int _imageCount;

        public DocxWriter(PageSettings page) => _page = page;

        public List<(string Name, byte[] Data)> Media { get; } = new();

        private static XElement El(string name, params object?[] content) => new(W + name, content);
        private static XAttribute At(string name, object value) => new(W + name, value);

        private string AddRel(string type, string target, bool external)
        {
            string id = "rId" + (_rels.Count + 3);   // rId1 = styles, rId2 = numbering
            _rels.Add((id, type, target, external));
            return id;
        }

        // ---- package parts

        public XDocument BuildDocument(FlowDocument doc)
        {
            var body = El("body");
            WriteBlocks(doc.Blocks, body);
            // Word wants a paragraph after a final table.
            if (body.LastNode is not XElement last || last.Name != W + "p") body.Add(El("p"));
            body.Add(El("sectPr",
                El("pgSz", At("w", (int)Math.Round(_page.WidthPt * 20)), At("h", (int)Math.Round(_page.HeightPt * 20))),
                El("pgMar", At("top", (int)Math.Round(_page.MarginPt * 20)), At("right", (int)Math.Round(_page.MarginPt * 20)),
                    At("bottom", (int)Math.Round(_page.MarginPt * 20)), At("left", (int)Math.Round(_page.MarginPt * 20)),
                    At("header", "720"), At("footer", "720"), At("gutter", "0"))));

            var root = new XElement(W + "document",
                new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "wp", WP.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "pic", PIC.NamespaceName),
                body);
            return Doc(root);
        }

        public XDocument BuildContentTypes()
        {
            var types = new XElement(CT + "Types",
                new XElement(CT + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(CT + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(CT + "Default", new XAttribute("Extension", "png"), new XAttribute("ContentType", "image/png")),
                new XElement(CT + "Default", new XAttribute("Extension", "jpeg"), new XAttribute("ContentType", "image/jpeg")),
                new XElement(CT + "Default", new XAttribute("Extension", "gif"), new XAttribute("ContentType", "image/gif")),
                new XElement(CT + "Default", new XAttribute("Extension", "bmp"), new XAttribute("ContentType", "image/bmp")),
                Override("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"),
                Override("/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"),
                Override("/word/numbering.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"),
                Override("/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml"),
                Override("/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml"));
            return Doc(types);

            static XElement Override(string part, string type) =>
                new(CT + "Override", new XAttribute("PartName", part), new XAttribute("ContentType", type));
        }

        public XDocument BuildDocumentRels()
        {
            var root = new XElement(PkgRel + "Relationships",
                new XElement(PkgRel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", RelStyles), new XAttribute("Target", "styles.xml")),
                new XElement(PkgRel + "Relationship", new XAttribute("Id", "rId2"), new XAttribute("Type", RelNumbering), new XAttribute("Target", "numbering.xml")));
            foreach (var (id, type, target, external) in _rels)
            {
                var rel = new XElement(PkgRel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", type), new XAttribute("Target", target));
                if (external) rel.Add(new XAttribute("TargetMode", "External"));
                root.Add(rel);
            }
            return Doc(root);
        }

        public XDocument BuildNumbering()
        {
            var root = new XElement(W + "numbering", new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName));
            root.Add(AbstractNum(0, bullets: true));
            root.Add(AbstractNum(1, bullets: false));
            root.Add(El("num", At("numId", 1), El("abstractNumId", At("val", 0))));
            foreach (var num in _nums) root.Add(num);
            return Doc(root);
        }

        private static XElement AbstractNum(int id, bool bullets)
        {
            var abs = El("abstractNum", At("abstractNumId", id),
                El("multiLevelType", At("val", "hybridMultilevel")));
            string[] bulletChars = { "•", "◦", "▪" };
            string[] numFormats = { "decimal", "lowerLetter", "lowerRoman" };
            for (int lvl = 0; lvl < 9; lvl++)
            {
                var level = El("lvl", At("ilvl", lvl),
                    El("start", At("val", 1)),
                    El("numFmt", At("val", bullets ? "bullet" : numFormats[lvl % 3])),
                    El("lvlText", At("val", bullets ? bulletChars[lvl % 3] : $"%{lvl + 1}.")),
                    El("lvlJc", At("val", "left")),
                    El("pPr", El("ind", At("left", 720 * (lvl + 1)), At("hanging", 360))));
                if (bullets)
                    level.Add(El("rPr", El("rFonts", At("ascii", DocStyles.DefaultFont), At("hAnsi", DocStyles.DefaultFont), At("hint", "default"))));
                abs.Add(level);
            }
            return abs;
        }

        // ---- blocks

        private void WriteBlocks(BlockCollection blocks, XElement parent)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph p: parent.Add(WriteParagraph(p)); break;
                    case List list: WriteList(list, 0, parent); break;
                    case Table table: parent.Add(WriteTable(table)); break;
                    case Section section: WriteBlocks(section.Blocks, parent); break;
                }
            }
        }

        private void WriteList(List list, int level, XElement parent)
        {
            int numId = IsBullet(list.MarkerStyle) ? 1 : NewNumberedList(level);
            foreach (ListItem item in list.ListItems)
            {
                bool first = true;
                foreach (Block block in item.Blocks)
                {
                    switch (block)
                    {
                        case Paragraph p:
                            parent.Add(first ? WriteParagraph(p, numId, level) : WriteParagraph(p));
                            first = false;
                            break;
                        case List nested:
                            WriteList(nested, level + 1, parent);
                            break;
                        case Table table:
                            parent.Add(WriteTable(table));
                            break;
                    }
                }
            }
        }

        private int NewNumberedList(int level)
        {
            int id = _nextNumId++;
            _nums.Add(El("num", At("numId", id), El("abstractNumId", At("val", 1)),
                El("lvlOverride", At("ilvl", level), El("startOverride", At("val", 1)))));
            return id;
        }

        private XElement WriteParagraph(Paragraph p, int numId = 0, int level = 0)
        {
            string style = DocStyles.Of(p);
            var pPr = El("pPr");
            if (style != DocStyles.Normal) pPr.Add(El("pStyle", At("val", style)));
            if (numId > 0) pPr.Add(El("numPr", El("ilvl", At("val", level)), El("numId", At("val", numId))));

            var spacing = El("spacing",
                At("before", Math.Max(0, Units.DipToTwips(p.Margin.Top))),
                At("after", Math.Max(0, Units.DipToTwips(p.Margin.Bottom))));
            if (!double.IsNaN(p.LineHeight) && p.LineHeight > 0 && p.FontSize > 0)
            {
                double natural = p.FontSize * (p.FontFamily?.LineSpacing ?? 1.33);
                double multiple = natural > 0 ? p.LineHeight / natural : 1;
                spacing.Add(At("line", (int)Math.Round(Math.Clamp(multiple, 0.5, 4) * 240)), At("lineRule", "auto"));
            }
            pPr.Add(spacing);

            string? jc = p.TextAlignment switch
            {
                TextAlignment.Center => "center",
                TextAlignment.Right => "right",
                TextAlignment.Justify => "both",
                _ => null,
            };
            if (jc != null) pPr.Add(El("jc", At("val", jc)));

            var para = El("p", pPr);
            WriteInlines(p.Inlines, para, style);
            return para;
        }

        private void WriteInlines(InlineCollection inlines, XElement parent, string style)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        WriteRun(run, parent, style);
                        break;
                    case LineBreak:
                        parent.Add(El("r", El("br")));
                        break;
                    case Hyperlink link:
                        string target = link.NavigateUri?.OriginalString ?? "";
                        if (target.Length == 0)
                        {
                            WriteInlines(link.Inlines, parent, style);
                            break;
                        }
                        var hyper = El("hyperlink", new XAttribute(R + "id", AddRel(RelHyperlink, Clean(target), true)), At("history", "1"));
                        WriteInlines(link.Inlines, hyper, style);
                        parent.Add(hyper);
                        break;
                    case Span span:
                        WriteInlines(span.Inlines, parent, style);
                        break;
                    case InlineUIContainer { Child: Image image }:
                        WriteImage(image, parent);
                        break;
                }
            }
        }

        private void WriteRun(Run run, XElement parent, string style)
        {
            string text = run.Text;
            if (string.IsNullOrEmpty(text)) return;
            XElement? props = RunProperties(run, style);

            var r = El("r");
            if (props != null) r.Add(props);

            var segment = new StringBuilder();
            void Flush()
            {
                if (segment.Length == 0) return;
                var t = El("t", new XAttribute(XNamespace.Xml + "space", "preserve"), Clean(segment.ToString()));
                r.Add(t);
                segment.Clear();
            }
            foreach (char c in text)
            {
                if (c == '\t') { Flush(); r.Add(El("tab")); }
                else if (c == '\n' || c == '\r' || c == '\u2028' || c == '\v') { Flush(); r.Add(El("br")); }
                else segment.Append(c);
            }
            Flush();
            if (r.Elements().Any(e => e.Name != W + "rPr")) parent.Add(r);
        }

        private static XElement? RunProperties(Run run, string style)
        {
            var props = El("rPr");

            string family = run.FontFamily?.Source ?? DocStyles.DefaultFont;
            int comma = family.IndexOf(',');
            if (comma > 0) family = family.Substring(0, comma).Trim();
            if (!string.Equals(family, DocStyles.DefaultFont, StringComparison.OrdinalIgnoreCase))
                props.Add(El("rFonts", At("ascii", family), At("hAnsi", family), At("cs", family), At("eastAsia", family)));

            bool bold = run.FontWeight.ToOpenTypeWeight() >= 600;
            if (bold != DocStyles.IsBold(style))
            {
                props.Add(bold ? El("b") : El("b", At("val", "0")));
                props.Add(bold ? El("bCs") : El("bCs", At("val", "0")));
            }
            if (run.FontStyle == FontStyles.Italic || run.FontStyle == FontStyles.Oblique)
                props.Add(El("i"));

            Decorations(run, out bool underline, out bool strike);
            if (strike) props.Add(El("strike"));

            if (SolidColor(run.Foreground) is { } color && color != Colors.Black)
                props.Add(El("color", At("val", Hex(color))));

            int halfPoints = (int)Math.Round(run.FontSize * 1.5);
            int styleHalfPoints = (int)Math.Round(DocStyles.SizePt(style) * 2);
            if (halfPoints > 0 && halfPoints != styleHalfPoints)
            {
                props.Add(El("sz", At("val", halfPoints)));
                props.Add(El("szCs", At("val", halfPoints)));
            }
            if (underline) props.Add(El("u", At("val", "single")));

            if (Highlight(run) is { } fill)
                props.Add(El("shd", At("val", "clear"), At("color", "auto"), At("fill", Hex(fill))));

            return props.HasElements ? props : null;
        }

        /// <summary>Underline / strikethrough live on the run or on any Span/Underline/Hyperlink around it.</summary>
        private static void Decorations(TextElement start, out bool underline, out bool strike)
        {
            underline = false;
            strike = false;
            for (TextElement? e = start; e != null; e = e.Parent as TextElement)
            {
                TextDecorationCollection? decorations = e switch
                {
                    Inline inline => inline.TextDecorations,
                    Paragraph paragraph => paragraph.TextDecorations,
                    _ => null,
                };
                if (decorations == null) continue;
                foreach (TextDecoration d in decorations)
                {
                    if (d.Location == TextDecorationLocation.Underline) underline = true;
                    else if (d.Location == TextDecorationLocation.Strikethrough) strike = true;
                }
            }
        }

        private static Color? Highlight(TextElement start)
        {
            for (TextElement? e = start; e != null; e = e.Parent as TextElement)
            {
                if (e.Background is SolidColorBrush { Color.A: > 0 } b && b.Opacity > 0) return b.Color;
                if (e is Paragraph) break;
            }
            return null;
        }

        // ---- images

        private void WriteImage(Image image, XElement parent)
        {
            if (image.Source is not BitmapSource bitmap) return;

            byte[] data;
            string ext;
            if (image.Tag is EmbeddedImage embedded)
            {
                data = embedded.Bytes;
                ext = embedded.Extension;
            }
            else
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                data = ms.ToArray();
                ext = "png";
            }

            double width = double.IsNaN(image.Width) ? bitmap.Width : image.Width;
            double height = double.IsNaN(image.Height) ? bitmap.Height : image.Height;
            if (width <= 0 || height <= 0) return;

            _imageCount++;
            string fileName = $"image{_imageCount}.{ext}";
            Media.Add((fileName, data));
            string rid = AddRel(RelImage, "media/" + fileName, false);

            long cx = Units.DipToEmu(width), cy = Units.DipToEmu(height);
            var drawing = new XElement(W + "drawing",
                new XElement(WP + "inline",
                    new XAttribute("distT", "0"), new XAttribute("distB", "0"), new XAttribute("distL", "0"), new XAttribute("distR", "0"),
                    new XElement(WP + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                    new XElement(WP + "effectExtent", new XAttribute("l", "0"), new XAttribute("t", "0"), new XAttribute("r", "0"), new XAttribute("b", "0")),
                    new XElement(WP + "docPr", new XAttribute("id", _imageCount), new XAttribute("name", $"Picture {_imageCount}")),
                    new XElement(WP + "cNvGraphicFramePr",
                        new XElement(A + "graphicFrameLocks", new XAttribute("noChangeAspect", "1"))),
                    new XElement(A + "graphic",
                        new XElement(A + "graphicData", new XAttribute("uri", PIC.NamespaceName),
                            new XElement(PIC + "pic",
                                new XElement(PIC + "nvPicPr",
                                    new XElement(PIC + "cNvPr", new XAttribute("id", "0"), new XAttribute("name", fileName)),
                                    new XElement(PIC + "cNvPicPr")),
                                new XElement(PIC + "blipFill",
                                    new XElement(A + "blip", new XAttribute(R + "embed", rid)),
                                    new XElement(A + "stretch", new XElement(A + "fillRect"))),
                                new XElement(PIC + "spPr",
                                    new XElement(A + "xfrm",
                                        new XElement(A + "off", new XAttribute("x", "0"), new XAttribute("y", "0")),
                                        new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                                    new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))))))));
            parent.Add(El("r", drawing));
        }

        // ---- tables

        private XElement WriteTable(Table table)
        {
            var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
            int columns = table.Columns.Count;
            if (columns == 0)
                columns = rows.Select(r => r.Cells.Sum(c => Math.Max(1, c.ColumnSpan))).DefaultIfEmpty(1).Max();
            columns = Math.Max(1, columns);

            int textTwips = (int)Math.Round(_page.TextWidthPt * 20);
            int columnTwips = Math.Max(300, textTwips / columns);

            XElement BorderEl(string side) => El(side, At("val", "single"), At("sz", "4"), At("space", "0"), At("color", "BFBFBF"));
            XElement MarginEl(string side, int w) => El(side, At("w", w), At("type", "dxa"));

            var tbl = El("tbl",
                El("tblPr",
                    El("tblW", At("w", columnTwips * columns), At("type", "dxa")),
                    El("tblBorders", BorderEl("top"), BorderEl("left"), BorderEl("bottom"), BorderEl("right"), BorderEl("insideH"), BorderEl("insideV")),
                    El("tblLayout", At("type", "fixed")),
                    El("tblCellMar", MarginEl("top", 60), MarginEl("left", 100), MarginEl("bottom", 60), MarginEl("right", 100))),
                El("tblGrid", Enumerable.Range(0, columns).Select(_ => El("gridCol", At("w", columnTwips))).ToArray()));

            foreach (TableRow row in rows)
            {
                var tr = El("tr");
                foreach (TableCell cell in row.Cells)
                {
                    int span = Math.Max(1, cell.ColumnSpan);
                    var tcPr = El("tcPr", El("tcW", At("w", columnTwips * span), At("type", "dxa")));
                    if (span > 1) tcPr.Add(El("gridSpan", At("val", span)));
                    if (SolidColor(cell.Background) is { } fill && fill != Colors.White)
                        tcPr.Add(El("shd", At("val", "clear"), At("color", "auto"), At("fill", Hex(fill))));

                    var tc = El("tc", tcPr);
                    WriteBlocks(cell.Blocks, tc);
                    if (tc.LastNode is not XElement lastNode || lastNode.Name != W + "p") tc.Add(El("p"));
                    tr.Add(tc);
                }
                if (tr.Elements(W + "tc").Any()) tbl.Add(tr);
            }
            return tbl;
        }
    }

    // ============================================================= reading

    /// <summary>Open a document written by <see cref="Save"/> (or by Word, as far as the supported subset goes).</summary>
    public static (FlowDocument Document, PageSettings Page) Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return new DocxReader(zip).Read();
    }

    private sealed class DocxReader
    {
        private readonly ZipArchive _zip;
        private readonly Dictionary<string, (string Type, string Target, bool External)> _rels = new();
        private readonly Dictionary<int, bool> _numIsBullet = new();
        private PageSettings _page = new();

        public DocxReader(ZipArchive zip) => _zip = zip;

        private XDocument? ReadXml(string name)
        {
            var entry = _zip.GetEntry(name);
            if (entry == null) return null;
            using var s = entry.Open();
            return XDocument.Load(s, LoadOptions.PreserveWhitespace);
        }

        public (FlowDocument, PageSettings) Read()
        {
            var main = ReadXml("word/document.xml") ?? throw new InvalidDataException("This file has no document part.");

            if (ReadXml("word/_rels/document.xml.rels")?.Root is { } relRoot)
                foreach (var rel in relRoot.Elements(PkgRel + "Relationship"))
                {
                    string? id = (string?)rel.Attribute("Id");
                    if (id == null) continue;
                    _rels[id] = ((string?)rel.Attribute("Type") ?? "", (string?)rel.Attribute("Target") ?? "",
                        string.Equals((string?)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase));
                }
            ReadNumbering(ReadXml("word/numbering.xml"));

            var body = main.Root?.Element(W + "body") ?? throw new InvalidDataException("This file has no document body.");
            if (body.Element(W + "sectPr") is { } sect) _page = ReadPage(sect);

            var doc = new FlowDocument();
            Configure(doc, _page);
            ReadBlocks(body.Elements(), doc.Blocks);
            if (doc.Blocks.Count == 0) doc.Blocks.Add(DocStyles.NewParagraph());
            return (doc, _page);
        }

        private static PageSettings ReadPage(XElement sect)
        {
            var page = new PageSettings();
            if (sect.Element(W + "pgSz") is { } size &&
                double.TryParse((string?)size.Attribute(W + "w"), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
                double.TryParse((string?)size.Attribute(W + "h"), NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
                w > 2000 && h > 2000)
            {
                page.WidthPt = w / 20;
                page.HeightPt = h / 20;
            }
            if (sect.Element(W + "pgMar") is { } mar &&
                double.TryParse((string?)mar.Attribute(W + "left"), NumberStyles.Float, CultureInfo.InvariantCulture, out double left) &&
                left >= 200 && left < page.WidthPt * 10)
                page.MarginPt = left / 20;
            return page;
        }

        private void ReadNumbering(XDocument? numbering)
        {
            if (numbering?.Root == null) return;
            var abstractBullet = new Dictionary<int, bool>();
            foreach (var abs in numbering.Root.Elements(W + "abstractNum"))
            {
                if (!int.TryParse((string?)abs.Attribute(W + "abstractNumId"), out int id)) continue;
                var level0 = abs.Elements(W + "lvl").FirstOrDefault(l => (string?)l.Attribute(W + "ilvl") == "0");
                string? fmt = (string?)level0?.Element(W + "numFmt")?.Attribute(W + "val");
                abstractBullet[id] = fmt is "bullet" or "none";
            }
            foreach (var num in numbering.Root.Elements(W + "num"))
            {
                if (!int.TryParse((string?)num.Attribute(W + "numId"), out int numId)) continue;
                if (int.TryParse((string?)num.Element(W + "abstractNumId")?.Attribute(W + "val"), out int absId) &&
                    abstractBullet.TryGetValue(absId, out bool bullet))
                    _numIsBullet[numId] = bullet;
            }
        }

        // ---- blocks

        private void ReadBlocks(IEnumerable<XElement> elements, BlockCollection target)
        {
            var stack = new List<(List List, int Level, int NumId)>();
            foreach (var el in elements)
            {
                try
                {
                    if (el.Name == W + "p")
                    {
                        var p = ReadParagraph(el, out int numId, out int level);
                        if (numId <= 0)
                        {
                            stack.Clear();
                            target.Add(p);
                        }
                        else
                            AddListParagraph(p, numId, level, stack, target);
                    }
                    else if (el.Name == W + "tbl")
                    {
                        stack.Clear();
                        target.Add(ReadTable(el));
                    }
                    else if (el.Name == W + "sdt" && el.Element(W + "sdtContent") is { } content)
                    {
                        stack.Clear();
                        ReadBlocks(content.Elements(), target);
                    }
                }
                catch (Exception ex)
                {
                    App.LogError("Skipped an element while opening a document", ex);
                }
            }
        }

        private void AddListParagraph(Paragraph p, int numId, int level, List<(List List, int Level, int NumId)> stack, BlockCollection target)
        {
            bool bullet = _numIsBullet.TryGetValue(numId, out bool b) ? b : true;

            while (stack.Count > 0 && stack[^1].Level > level) stack.RemoveAt(stack.Count - 1);
            // A different list at the same depth starts a fresh list (numbering restarts).
            if (stack.Count > 0 && stack[^1].Level == level && stack[^1].NumId != numId)
            {
                if (level == 0) stack.Clear();
                else stack.RemoveAt(stack.Count - 1);
            }

            if (stack.Count == 0 || stack[^1].Level < level)
            {
                var list = new List
                {
                    MarkerStyle = bullet
                        ? (level == 0 ? TextMarkerStyle.Disc : level == 1 ? TextMarkerStyle.Circle : TextMarkerStyle.Square)
                        : (level == 0 ? TextMarkerStyle.Decimal : level == 1 ? TextMarkerStyle.LowerLatin : TextMarkerStyle.LowerRoman),
                };
                if (stack.Count == 0)
                    target.Add(list);
                else
                {
                    var outer = stack[^1].List;
                    if (outer.ListItems.LastListItem is { } lastItem)
                        lastItem.Blocks.Add(list);
                    else
                    {
                        var holder = new ListItem();
                        holder.Blocks.Add(list);
                        outer.ListItems.Add(holder);
                    }
                }
                stack.Add((list, level, numId));
            }

            var item = new ListItem();
            item.Blocks.Add(p);
            stack[^1].List.ListItems.Add(item);
        }

        private Paragraph ReadParagraph(XElement w_p, out int numId, out int level)
        {
            numId = 0;
            level = 0;
            var pPr = w_p.Element(W + "pPr");
            string style = DocStyles.Normalize((string?)pPr?.Element(W + "pStyle")?.Attribute(W + "val"));

            var p = new Paragraph();
            DocStyles.Apply(p, style);

            if (pPr != null)
            {
                if (pPr.Element(W + "numPr") is { } numPr)
                {
                    int.TryParse((string?)numPr.Element(W + "numId")?.Attribute(W + "val"), out numId);
                    int.TryParse((string?)numPr.Element(W + "ilvl")?.Attribute(W + "val"), out level);
                    level = Math.Clamp(level, 0, 8);
                }
                switch ((string?)pPr.Element(W + "jc")?.Attribute(W + "val"))
                {
                    case "center": p.TextAlignment = TextAlignment.Center; break;
                    case "right" or "end": p.TextAlignment = TextAlignment.Right; break;
                    case "both" or "distribute": p.TextAlignment = TextAlignment.Justify; break;
                }
                if (pPr.Element(W + "spacing") is { } spacing)
                {
                    double top = p.Margin.Top, bottom = p.Margin.Bottom;
                    if (double.TryParse((string?)spacing.Attribute(W + "before"), NumberStyles.Float, CultureInfo.InvariantCulture, out double before))
                        top = Units.TwipsToDip(before);
                    if (double.TryParse((string?)spacing.Attribute(W + "after"), NumberStyles.Float, CultureInfo.InvariantCulture, out double after))
                        bottom = Units.TwipsToDip(after);
                    p.Margin = new Thickness(0, top, 0, bottom);

                    if (double.TryParse((string?)spacing.Attribute(W + "line"), NumberStyles.Float, CultureInfo.InvariantCulture, out double line) &&
                        ((string?)spacing.Attribute(W + "lineRule") ?? "auto") == "auto" && line > 0 && Math.Abs(line - 240) > 6)
                        p.LineHeight = (line / 240.0) * p.FontSize * p.FontFamily.LineSpacing;
                }
            }

            ReadInlines(p.Inlines, w_p.Elements());
            return p;
        }

        private void ReadInlines(InlineCollection target, IEnumerable<XElement> elements)
        {
            foreach (var el in elements)
            {
                switch (el.Name.LocalName)
                {
                    case "r":
                        foreach (var inline in ReadRun(el)) target.Add(inline);
                        break;
                    case "hyperlink":
                        {
                            string? rid = (string?)el.Attribute(R + "id");
                            Uri? uri = null;
                            if (rid != null && _rels.TryGetValue(rid, out var rel) && rel.External)
                                Uri.TryCreate(rel.Target, UriKind.Absolute, out uri);
                            if (uri != null)
                            {
                                var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.OriginalString };
                                ReadInlines(link.Inlines, el.Elements());
                                target.Add(link);
                            }
                            else
                            {
                                var span = new Span();
                                ReadInlines(span.Inlines, el.Elements());
                                target.Add(span);
                            }
                            break;
                        }
                    case "ins":
                    case "smartTag":
                    case "fldSimple":
                        ReadInlines(target, el.Elements());
                        break;
                }
            }
        }

        private IEnumerable<Inline> ReadRun(XElement r)
        {
            var result = new List<Inline>();
            var rPr = r.Element(W + "rPr");
            var text = new StringBuilder();

            void FlushText()
            {
                if (text.Length == 0) return;
                var run = new Run(text.ToString());
                ApplyRunProperties(run, rPr);
                result.Add(run);
                text.Clear();
            }

            foreach (var c in r.Elements())
            {
                switch (c.Name.LocalName)
                {
                    case "t": text.Append((string)c); break;
                    case "tab": text.Append('\t'); break;
                    case "br":
                        if ((string?)c.Attribute(W + "type") is "page" or "column") break;
                        FlushText();
                        result.Add(new LineBreak());
                        break;
                    case "drawing":
                        FlushText();
                        if (ReadImage(c) is { } image) result.Add(image);
                        break;
                }
            }
            FlushText();
            return result;
        }

        private static bool Flag(XElement? e)
        {
            if (e == null) return false;
            string? v = (string?)e.Attribute(W + "val");
            return v == null || !(v is "0" or "false" or "off" or "none");
        }

        private static void ApplyRunProperties(Run run, XElement? rPr)
        {
            if (rPr == null) return;

            if (rPr.Element(W + "b") is { } b) run.FontWeight = Flag(b) ? FontWeights.Bold : FontWeights.Normal;
            if (rPr.Element(W + "i") is { } i && Flag(i)) run.FontStyle = FontStyles.Italic;

            bool underline = Flag(rPr.Element(W + "u"));
            bool strike = Flag(rPr.Element(W + "strike"));
            if (underline || strike)
            {
                var decorations = new TextDecorationCollection();
                if (underline) foreach (var d in TextDecorations.Underline) decorations.Add(d);
                if (strike) foreach (var d in TextDecorations.Strikethrough) decorations.Add(d);
                run.TextDecorations = decorations;
            }

            if (ParseHex((string?)rPr.Element(W + "color")?.Attribute(W + "val")) is { } color)
                run.Foreground = Frozen(color);

            if (double.TryParse((string?)rPr.Element(W + "sz")?.Attribute(W + "val"), NumberStyles.Float, CultureInfo.InvariantCulture, out double halfPoints) && halfPoints > 0)
                run.FontSize = halfPoints * (Units.DipPerPt / 2);

            if ((string?)rPr.Element(W + "rFonts")?.Attribute(W + "ascii") is { Length: > 0 } family)
                run.FontFamily = new FontFamily(family);

            var shd = rPr.Element(W + "shd");
            if (ParseHex((string?)shd?.Attribute(W + "fill")) is { } fill)
                run.Background = Frozen(fill);
            else if (HighlightColor((string?)rPr.Element(W + "highlight")?.Attribute(W + "val")) is { } highlight)
                run.Background = Frozen(highlight);
        }

        private static Color? HighlightColor(string? name) => name switch
        {
            "yellow" => Color.FromRgb(0xFF, 0xFF, 0x00),
            "green" => Color.FromRgb(0x00, 0xFF, 0x00),
            "cyan" => Color.FromRgb(0x00, 0xFF, 0xFF),
            "magenta" => Color.FromRgb(0xFF, 0x00, 0xFF),
            "blue" => Color.FromRgb(0x00, 0x00, 0xFF),
            "red" => Color.FromRgb(0xFF, 0x00, 0x00),
            "darkYellow" => Color.FromRgb(0x80, 0x80, 0x00),
            "lightGray" => Color.FromRgb(0xC0, 0xC0, 0xC0),
            _ => null,
        };

        private Inline? ReadImage(XElement drawing)
        {
            string? rid = (string?)drawing.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");
            if (rid == null || !_rels.TryGetValue(rid, out var rel) || rel.External) return null;

            string entryName = rel.Target.StartsWith('/') ? rel.Target.TrimStart('/') : "word/" + rel.Target;
            var entry = _zip.GetEntry(entryName);
            if (entry == null) return null;

            byte[] bytes;
            using (var s = entry.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }

            BitmapImage bitmap;
            try { bitmap = LoadBitmap(bytes); }
            catch { return null; }

            var extent = drawing.Descendants(WP + "extent").FirstOrDefault();
            double width = bitmap.Width, height = bitmap.Height;
            if (extent != null &&
                double.TryParse((string?)extent.Attribute("cx"), NumberStyles.Float, CultureInfo.InvariantCulture, out double cx) &&
                double.TryParse((string?)extent.Attribute("cy"), NumberStyles.Float, CultureInfo.InvariantCulture, out double cy) &&
                cx > 0 && cy > 0)
            {
                width = Units.EmuToDip(cx);
                height = Units.EmuToDip(cy);
            }
            double max = _page.TextWidthDip;
            if (width > max && max > 0)
            {
                height *= max / width;
                width = max;
            }

            string ext = ImageExtension(bytes);
            var image = new Image
            {
                Source = bitmap,
                Width = width,
                Height = height,
                Stretch = Stretch.Uniform,
                Tag = ext.Length > 0 ? new EmbeddedImage(bytes, ext) : null,
            };
            return new InlineUIContainer(image);
        }

        // ---- tables

        private Table ReadTable(XElement tbl)
        {
            var table = new Table { CellSpacing = 0 };
            int columns = tbl.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() ?? 0;
            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            foreach (var tr in tbl.Elements(W + "tr"))
            {
                var row = new TableRow();
                int used = 0;
                foreach (var tc in tr.Elements(W + "tc"))
                {
                    var tcPr = tc.Element(W + "tcPr");
                    var cell = new TableCell
                    {
                        BorderBrush = Frozen(Color.FromRgb(0xBF, 0xBF, 0xBF)),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(6, 3, 6, 3),
                    };
                    if (int.TryParse((string?)tcPr?.Element(W + "gridSpan")?.Attribute(W + "val"), out int span) && span > 1)
                        cell.ColumnSpan = span;
                    if (ParseHex((string?)tcPr?.Element(W + "shd")?.Attribute(W + "fill")) is { } fill)
                        cell.Background = Frozen(fill);

                    ReadBlocks(tc.Elements(), cell.Blocks);
                    if (cell.Blocks.Count == 0) cell.Blocks.Add(NewCellParagraph());
                    row.Cells.Add(cell);
                    used += Math.Max(1, cell.ColumnSpan);
                }
                if (row.Cells.Count > 0) group.Rows.Add(row);
                columns = Math.Max(columns, used);
            }

            for (int i = 0; i < Math.Max(1, columns); i++)
                table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            return table;
        }
    }

    // ------------------------------------------------------------- tables made by the editor

    public static Paragraph NewCellParagraph()
    {
        var p = DocStyles.NewParagraph();
        p.Margin = new Thickness(0);
        return p;
    }

    /// <summary>A new table of <paramref name="rows"/> x <paramref name="columns"/>; the first row is a shaded header.</summary>
    public static Table NewTable(int rows, int columns)
    {
        var table = new Table { CellSpacing = 0 };
        for (int c = 0; c < columns; c++)
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (int r = 0; r < rows; r++)
            group.Rows.Add(NewRow(columns, header: r == 0));
        return table;
    }

    public static TableRow NewRow(int columns, bool header = false)
    {
        var row = new TableRow();
        for (int c = 0; c < columns; c++)
            row.Cells.Add(NewCell(header));
        return row;
    }

    public static TableCell NewCell(bool header = false)
    {
        var paragraph = NewCellParagraph();
        if (header) paragraph.FontWeight = FontWeights.Bold;
        var cell = new TableCell(paragraph)
        {
            BorderBrush = Frozen(Color.FromRgb(0xBF, 0xBF, 0xBF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 3, 6, 3),
        };
        if (header) cell.Background = Frozen(Color.FromRgb(0xF0, 0xF0, 0xEC));
        return cell;
    }
}
