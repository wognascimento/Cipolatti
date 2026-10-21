namespace Cipolatti.API.Infrastructure;

public interface IFileStorage
{
    Task<string> SaveCoverAsWebpAsync(int idTema, Stream content, CancellationToken ct);
    Task<string> SaveGalleryCoverAsWebpAsync(int idTema, Stream content, CancellationToken ct);
    Task<string> SaveConceptAsWebpAsync(int idConceito, Stream content, CancellationToken ct);
    Task<StoredPdfFile> SavePdfOriginalAsync(int idTema, long idPdf, Stream content, CancellationToken ct);
    Task<StoredPdfPage> SavePdfPageAsync(int idTema, long idPdf, string version, int pageNumber,
        ReadOnlyMemory<byte> webp, int width, int height, CancellationToken ct);
    string GetLocalPath(string relativePath);
    Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct);
    Task PersistPdfJobAsync(PdfProcessingJob job, CancellationToken ct);
    Task<IReadOnlyCollection<PdfProcessingJob>> ReadPersistedPdfJobsAsync(CancellationToken ct);
    Task CompletePdfJobAsync(PdfProcessingJob job, CancellationToken ct);
}

public sealed record StoredPdfFile(string RelativePath, string Version);
public sealed record StoredPdfPage(int PageNumber, string RelativePath, int Width, int Height, long SizeBytes);
