using System.Runtime.InteropServices;
using Cipolatti.API.Infrastructure;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Cipolatti.API.Services;

public sealed class PdfiumRenderer(IFileStorage storage, IOptions<CatalogoStorageOptions> settings) : IPdfRenderer
{
    private static readonly SemaphoreSlim RenderLock = new(1, 1);
    private readonly CatalogoStorageOptions options = settings.Value;

    public async Task<IReadOnlyCollection<StoredPdfPage>> RenderAsync(int idTema, long idPdf, string originalPath,
        string version, CancellationToken ct)
    {
        await RenderLock.WaitAsync(ct);
        try
        {
            using var document = DocLib.Instance.GetDocReader(storage.GetLocalPath(originalPath),
                new PageDimensions(options.PdfRenderWidth, options.PdfRenderHeight));
            var count = document.GetPageCount();
            if (count is < 1 or > 500) throw new InvalidDataException("O PDF deve possuir entre 1 e 500 paginas.");
            var result = new List<StoredPdfPage>(count);
            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                using var page = document.GetPageReader(index);
                var width = page.GetPageWidth();
                var height = page.GetPageHeight();
                var pixels = page.GetImage();
                if (pixels.Length != width * height * 4) throw new InvalidDataException("A pagina renderizada possui tamanho inesperado.");
                using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Webp, options.PdfWebpQuality)
                    ?? throw new InvalidDataException("Nao foi possivel codificar a pagina em WebP.");
                result.Add(await storage.SavePdfPageAsync(idTema, idPdf, version, index + 1,
                    encoded.ToArray(), width, height, ct));
            }
            return result;
        }
        finally { RenderLock.Release(); }
    }
}

