using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Graphite.Core.Rendering;

namespace Graphite.App.Services;

/// <summary>Prints via the system print dialog. Pages are rasterized by PDFium
/// at 300 dpi (with annotations baked in) and scaled to fit the printable area.</summary>
public static class PrintService
{
    public static void Print(byte[] pdfWithAnnotations, string jobName)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        var renderer = new PdfRenderer(pdfWithAnnotations);
        var paginator = new PdfPaginator(renderer,
            new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight));
        dlg.PrintDocument(paginator, jobName);
    }

    private sealed class PdfPaginator : DocumentPaginator
    {
        private readonly PdfRenderer _renderer;
        private readonly Size _area;

        public PdfPaginator(PdfRenderer renderer, Size area)
        {
            _renderer = renderer;
            _area = area;
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => _renderer.PageCount;
        public override Size PageSize { get => _area; set { } }
        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            var (wPt, hPt) = _renderer.PageSizes[pageNumber];

            // Fit the page into the printable area (device-independent pixels, 96/inch).
            double fit = Math.Min(_area.Width / (wPt * 96.0 / 72.0),
                                  _area.Height / (hPt * 96.0 / 72.0));
            double dipW = wPt * 96.0 / 72.0 * fit;
            double dipH = hPt * 96.0 / 72.0 * fit;

            // Rasterize at 300 dpi for crisp output.
            var rp = _renderer.Render(pageNumber, 300.0 / 72.0 * fit);
            var bmp = BitmapSource.Create(rp.Width, rp.Height, 96, 96,
                PixelFormats.Pbgra32, null, rp.Bgra, rp.Width * 4);
            bmp.Freeze();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var rect = new Rect((_area.Width - dipW) / 2, (_area.Height - dipH) / 2, dipW, dipH);
                dc.DrawImage(bmp, rect);
            }
            return new DocumentPage(visual, _area, new Rect(_area), new Rect(_area));
        }
    }
}
