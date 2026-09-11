namespace Cipolatti.API.Infrastructure;

public sealed class CatalogoStorageOptions
{
    public string RootPath { get; set; } = "storage";
    public long MaxCoverBytes { get; set; } = 20 * 1024 * 1024;
    public int MaxCoverDimension { get; set; } = 2400;
    public int WebpQuality { get; set; } = 85;
    public long MaxPdfBytes { get; set; } = 150 * 1024 * 1024;
    public int PdfRenderWidth { get; set; } = 1920;
    public int PdfRenderHeight { get; set; } = 2560;
    public int PdfWebpQuality { get; set; } = 86;
}
