using Cipolatti.API.Models.Catalogo;

namespace Cipolatti.API.Services;

public interface ICatalogoPdfAdminService
{
    Task<IReadOnlyCollection<AdminTemaPdfDto>?> ListarAsync(int idTema, CancellationToken ct);
    Task<AdminTemaPdfDto?> CriarAsync(int idTema, CriarTemaPdfRequest request, Stream content, string contentType, string? usuario, CancellationToken ct);
    Task<AdminTemaPdfDto?> AtualizarAsync(long idPdf, AtualizarTemaPdfRequest request, string? usuario, CancellationToken ct);
    Task<bool> SubstituirAsync(long idPdf, Stream content, string contentType, string? usuario, CancellationToken ct);
    Task<bool> ReprocessarAsync(long idPdf, string? usuario, CancellationToken ct);
    Task<bool> UsarPrimeiraPaginaComoCapaAsync(long idPdf, string? usuario, CancellationToken ct);
    Task<bool> DesativarAsync(long idPdf, string? usuario, CancellationToken ct);
    Task<bool> ReordenarAsync(IReadOnlyCollection<ReordenarTemaPdfItem> items, string? usuario, CancellationToken ct);
    Task<string?> ObterPaginaPublicaAsync(long idPdf, int numero, CancellationToken ct);
    Task<(string Path, string FileName)?> ObterDownloadPublicoAsync(long idPdf, CancellationToken ct);
}
