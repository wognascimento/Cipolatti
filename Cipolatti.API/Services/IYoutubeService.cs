using Cipolatti.API.Models.Catalogo;
namespace Cipolatti.API.Services;
public interface IYoutubeService
{
    Task<YoutubeStatusDto> GetStatusAsync(CancellationToken ct);
    string CreateAuthorizationUrl(string? usuario);
    Task<string> CompleteAuthorizationAsync(string code,string state,CancellationToken ct);
    Task<AdminTemaVideoDto> UploadVideoAsync(int idTema, YoutubeUploadRequest request, Stream video, string contentType, long length, Stream? thumbnail, string? thumbnailContentType, string? usuario, CancellationToken ct);
}
