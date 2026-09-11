using Cipolatti.API.Infrastructure;

namespace Cipolatti.API.Services;

public interface IPdfRenderer
{
    Task<IReadOnlyCollection<StoredPdfPage>> RenderAsync(int idTema, long idPdf, string originalPath,
        string version, CancellationToken ct);
}

