using Microsoft.Extensions.Options;
using SkiaSharp;
using System.Text.Json;

namespace Cipolatti.API.Infrastructure;

public sealed class LocalFileStorage : IFileStorage
{
    private readonly string root;
    private readonly CatalogoStorageOptions options;

    public LocalFileStorage(IOptions<CatalogoStorageOptions> options, IWebHostEnvironment environment)
    {
        this.options = options.Value;
        root = Path.GetFullPath(Path.IsPathRooted(this.options.RootPath)
            ? this.options.RootPath
            : Path.Combine(environment.ContentRootPath, this.options.RootPath));
    }

    public async Task<string> SaveCoverAsWebpAsync(int idTema, Stream content, CancellationToken ct)
    {
        var directory = SafePath("catalogo", "temas", idTema.ToString(), "capa");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, $"upload-{Guid.NewGuid():N}.tmp");
        var output = Path.Combine(directory, $"capa-{Guid.NewGuid():N}.webp");
        var final = Path.Combine(directory, "capa.webp");
        try
        {
            await using (var file = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > options.MaxCoverBytes) throw new InvalidDataException("A imagem excede o limite permitido.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            ValidateSignature(input);
            using var source = SKBitmap.Decode(input) ?? throw new InvalidDataException("O conteúdo não é uma imagem válida.");
            if (source.Width <= 0 || source.Height <= 0 || source.Width > 12000 || source.Height > 12000)
                throw new InvalidDataException("As dimensões da imagem são inválidas.");
            var scale = Math.Min(1d, options.MaxCoverDimension / (double)Math.Max(source.Width, source.Height));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            using var resized = scale < 1 ? source.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default)
                ?? throw new InvalidDataException("Não foi possível redimensionar a imagem.") : source.Copy();
            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, options.WebpQuality)
                ?? throw new InvalidDataException("Não foi possível converter a imagem para WebP.");
            await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                encoded.SaveTo(target);
            File.Move(output, final, true);
            return $"catalogo/temas/{idTema}/capa/capa.webp";
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
        }
    }

    public async Task<string> SaveGalleryCoverAsWebpAsync(int idTema, Stream content, CancellationToken ct)
    {
        var directory = SafePath("catalogo", "temas", idTema.ToString(), "capas");
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var input = Path.Combine(directory, $"upload-{id}.tmp");
        var output = Path.Combine(directory, $"{id}.webp");
        try
        {
            await using (var file = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0) { total += read; if (total > options.MaxCoverBytes) throw new InvalidDataException("A imagem excede o limite permitido."); await file.WriteAsync(buffer.AsMemory(0, read), ct); }
            }
            ValidateSignature(input);
            using var source = SKBitmap.Decode(input) ?? throw new InvalidDataException("O conteúdo não é uma imagem válida.");
            if (source.Width <= 0 || source.Height <= 0 || source.Width > 12000 || source.Height > 12000) throw new InvalidDataException("As dimensões da imagem são inválidas.");
            var scale = Math.Min(1d, options.MaxCoverDimension / (double)Math.Max(source.Width, source.Height));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale)); var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            using var resized = scale < 1 ? source.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default) ?? throw new InvalidDataException("Não foi possível redimensionar a imagem.") : source.Copy();
            using var image = SKImage.FromBitmap(resized); using var encoded = image.Encode(SKEncodedImageFormat.Webp, options.WebpQuality) ?? throw new InvalidDataException("Não foi possível converter a imagem para WebP.");
            await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true)) encoded.SaveTo(target);
            return $"catalogo/temas/{idTema}/capas/{id}.webp";
        }
        finally { TryDelete(input); }
    }

    public async Task<string> SaveConceptAsWebpAsync(int idConceito, Stream content, CancellationToken ct)
    {
        var directory=SafePath("catalogo","conceitos",idConceito.ToString()); Directory.CreateDirectory(directory);
        var input=Path.Combine(directory,$"upload-{Guid.NewGuid():N}.tmp"); var output=Path.Combine(directory,$"imagem-{Guid.NewGuid():N}.webp"); var final=Path.Combine(directory,"imagem.webp");
        try {
            await using(var file=new FileStream(input,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true)){var buffer=new byte[81920];long total=0;int read;while((read=await content.ReadAsync(buffer,ct))>0){total+=read;if(total>options.MaxCoverBytes)throw new InvalidDataException("A imagem excede o limite permitido.");await file.WriteAsync(buffer.AsMemory(0,read),ct);}}
            ValidateSignature(input); using var source=SKBitmap.Decode(input)??throw new InvalidDataException("O conteúdo não é uma imagem válida."); if(source.Width<=0||source.Height<=0||source.Width>12000||source.Height>12000)throw new InvalidDataException("As dimensões da imagem são inválidas.");
            var scale=Math.Min(1d,options.MaxCoverDimension/(double)Math.Max(source.Width,source.Height));var width=Math.Max(1,(int)Math.Round(source.Width*scale));var height=Math.Max(1,(int)Math.Round(source.Height*scale));using var resized=scale<1?source.Resize(new SKImageInfo(width,height),SKSamplingOptions.Default)??throw new InvalidDataException("Não foi possível redimensionar a imagem."):source.Copy();using var image=SKImage.FromBitmap(resized);using var encoded=image.Encode(SKEncodedImageFormat.Webp,options.WebpQuality)??throw new InvalidDataException("Não foi possível converter a imagem para WebP.");await using(var target=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))encoded.SaveTo(target);File.Move(output,final,true);return $"catalogo/conceitos/{idConceito}/imagem.webp";
        } finally { TryDelete(input);TryDelete(output); }
    }

    public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = SafePath(relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries));
        Stream? stream = File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true) : null;
        return Task.FromResult(stream);
    }

    public async Task<StoredPdfFile> SavePdfOriginalAsync(int idTema, long idPdf, Stream content, CancellationToken ct)
    {
        var version = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var directory = SafePath("catalogo", "temas", idTema.ToString(), "pdfs", idPdf.ToString(), "versions", version, "original");
        Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, "apresentacao.pdf");
        var temp = final + ".tmp";
        try
        {
            await using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > options.MaxPdfBytes) throw new InvalidDataException("O PDF excede o limite permitido.");
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await file.FlushAsync(ct);
            if (total < 8) throw new InvalidDataException("Arquivo PDF incompleto.");
            file.Position = 0;
            var signature = new byte[5];
            if (await file.ReadAsync(signature, ct) != 5 || !signature.AsSpan().SequenceEqual("%PDF-"u8))
                throw new InvalidDataException("O arquivo informado nao possui assinatura PDF valida.");
            file.Close();
            File.Move(temp, final);
            return new StoredPdfFile(Path.GetRelativePath(root, final).Replace(Path.DirectorySeparatorChar, '/'), version);
        }
        finally { TryDelete(temp); }
    }

    public async Task<StoredPdfPage> SavePdfPageAsync(int idTema, long idPdf, string version, int pageNumber,
        ReadOnlyMemory<byte> webp, int width, int height, CancellationToken ct)
    {
        if (pageNumber <= 0 || width <= 0 || height <= 0 || !version.All(ch => char.IsLetterOrDigit(ch) || ch == '-'))
            throw new InvalidOperationException("Metadados da pagina invalidos.");
        var directory = SafePath("catalogo", "temas", idTema.ToString(), "pdfs", idPdf.ToString(), "versions", version, "paginas");
        Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, $"{pageNumber:000}.webp");
        var temp = final + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, webp.ToArray(), ct);
            File.Move(temp, final, true);
            var relative = Path.GetRelativePath(root, final).Replace(Path.DirectorySeparatorChar, '/');
            return new StoredPdfPage(pageNumber, relative, width, height, webp.Length);
        }
        finally { TryDelete(temp); }
    }

    public string GetLocalPath(string relativePath) => SafePath(relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries));

    public async Task PersistPdfJobAsync(PdfProcessingJob job,CancellationToken ct)
    {
        if(!job.Replacement||string.IsNullOrWhiteSpace(job.Version))return;
        var directory=SafePath("catalogo","jobs","pdf");Directory.CreateDirectory(directory);
        var final=Path.Combine(directory,$"{job.IdPdf}-{job.Version}.json");var temp=final+".tmp";
        try{await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(job),ct);File.Move(temp,final,true);}finally{TryDelete(temp);}
    }

    public async Task<IReadOnlyCollection<PdfProcessingJob>> ReadPersistedPdfJobsAsync(CancellationToken ct)
    {
        var directory=SafePath("catalogo","jobs","pdf");if(!Directory.Exists(directory))return [];
        var jobs=new List<PdfProcessingJob>();
        foreach(var file in Directory.EnumerateFiles(directory,"*.json"))
        {ct.ThrowIfCancellationRequested();try{var job=JsonSerializer.Deserialize<PdfProcessingJob>(await File.ReadAllTextAsync(file,ct));if(job is not null&&job.IdPdf>0&&job.Version is not null&&job.Version.All(ch=>char.IsLetterOrDigit(ch)||ch=='-'))jobs.Add(job);else TryDelete(file);}catch(JsonException){TryDelete(file);}}
        return jobs;
    }

    public Task CompletePdfJobAsync(PdfProcessingJob job,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();if(job.Replacement&&!string.IsNullOrWhiteSpace(job.Version))TryDelete(SafePath("catalogo","jobs","pdf",$"{job.IdPdf}-{job.Version}.json"));return Task.CompletedTask;}

    private string SafePath(params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine([root, .. parts]));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Caminho de storage inválido.");
        return path;
    }

    private static void ValidateSignature(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) < 12) throw new InvalidDataException("Arquivo de imagem incompleto.");
        var jpeg = header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;
        var png = header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var webp = header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8);
        if (!jpeg && !png && !webp) throw new InvalidDataException("Formato permitido: JPEG, PNG ou WebP.");
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
