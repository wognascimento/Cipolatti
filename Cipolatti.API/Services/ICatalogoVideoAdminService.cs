using Cipolatti.API.Models.Catalogo;

namespace Cipolatti.API.Services;

public interface ICatalogoVideoAdminService
{
    Task<IReadOnlyCollection<AdminTemaVideoDto>?> ListarAsync(int idTema,CancellationToken ct);
    Task<AdminTemaVideoDto?> CriarAsync(int idTema,CriarTemaVideoRequest request,string? usuario,CancellationToken ct);
    Task<AdminTemaVideoDto?> AtualizarAsync(long idVideo,AtualizarTemaVideoRequest request,string? usuario,CancellationToken ct);
    Task<bool> DesativarAsync(long idVideo,string? usuario,CancellationToken ct);
    Task<bool> ReordenarAsync(IReadOnlyCollection<ReordenarTemaVideoItem> items,string? usuario,CancellationToken ct);
}

