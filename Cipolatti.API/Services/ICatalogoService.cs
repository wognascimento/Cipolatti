using Cipolatti.API.Models;
using Cipolatti.API.Models.Catalogo;

namespace Cipolatti.API.Services;

public interface ICatalogoService
{
    Task<PagedResult<TemaResumoDto>> ListarTemasAsync(CatalogoTemasFilter filter, CancellationToken ct);
    Task<TemaDetalheDto?> ObterTemaAsync(string slug, CancellationToken ct);
    Task<IReadOnlyCollection<ConceitoDto>> ListarConceitosAsync(CancellationToken ct);
    Task<IReadOnlyCollection<string>> ListarFaixasPrecoAsync(CancellationToken ct);
    Task<IReadOnlyCollection<TemaPdfDto>?> ListarPdfsAsync(string slug, CancellationToken ct);
    Task<IReadOnlyCollection<TemaPdfPaginaDto>?> ListarPaginasAsync(long idPdf, CancellationToken ct);
    Task<IReadOnlyCollection<TemaVideoDto>?> ListarVideosAsync(string slug, CancellationToken ct);
    Task<IReadOnlyCollection<TemaResumoDto>?> ListarRelacionadosAsync(string slug, CancellationToken ct);
}
