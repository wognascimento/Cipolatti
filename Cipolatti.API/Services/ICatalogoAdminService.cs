using Cipolatti.API.Models;
using Cipolatti.API.Models.Catalogo;

namespace Cipolatti.API.Services;

public interface ICatalogoAdminService
{
    Task<PagedResult<AdminTemaResumoDto>> ListarAsync(string? busca, int page, int pageSize, CancellationToken ct);
    Task<AdminTemaDetalheDto?> ObterAsync(int idTema, CancellationToken ct);
    Task<IReadOnlyCollection<ConceitoDto>> ListarConceitosAsync(CancellationToken ct);
    Task<ConceitoDto> CriarConceitoAsync(SalvarConceitoRequest request, string? usuario, CancellationToken ct);
    Task<ConceitoDto?> AtualizarConceitoAsync(int idConceito, SalvarConceitoRequest request, string? usuario, CancellationToken ct);
    Task<bool> ExcluirConceitoAsync(int idConceito, bool desassociar, string? usuario, CancellationToken ct);
    Task<AdminTemaDetalheDto?> AtualizarAsync(int idTema, AtualizarTemaCatalogoRequest request, string? usuario, CancellationToken ct);
    Task<string?> AtualizarCapaAsync(int idTema, Stream content, string contentType, string? usuario, CancellationToken ct);
    Task<string?> ObterCapaPublicaAsync(int idTema, CancellationToken ct);
    Task<bool> AtualizarImagemConceitoAsync(int idConceito, Stream content, string contentType, string? usuario, CancellationToken ct);
    Task<string?> ObterImagemConceitoAsync(int idConceito, CancellationToken ct);
}
