using System.ComponentModel.DataAnnotations;

namespace Cipolatti.API.Models.Catalogo;

public sealed record AdminTemaVideoDto(long IdTemaVideo,int IdTema,string Titulo,string? Descricao,
    string? YoutubeVideoId,string? YoutubeUrl,string? ThumbnailUrl,int Ordem,bool Ativo,string Status,
    string? PrivacyStatus,string? MensagemErro,DateTime? DataAltera);

public class CriarTemaVideoRequest
{
    [Required,StringLength(200)]public string Titulo{get;init;}=string.Empty;
    [StringLength(10000)]public string? Descricao{get;init;}
    [Required,StringLength(500)]public string YoutubeUrlOuId{get;init;}=string.Empty;
    [StringLength(500)]public string? ThumbnailUrl{get;init;}
    [RegularExpression("^(public|unlisted|private)$")]public string PrivacyStatus{get;init;}="unlisted";
}

public sealed class AtualizarTemaVideoRequest:CriarTemaVideoRequest
{
    [Range(0,int.MaxValue)]public int Ordem{get;init;}
    public bool Ativo{get;init;}=true;
}

public sealed record ReordenarTemaVideoItem(long IdTemaVideo,int Ordem);

public sealed class YoutubeUploadRequest
{
    [Required,StringLength(200)] public string Titulo { get; init; } = string.Empty;
    [StringLength(10000)] public string? Descricao { get; init; }
    [RegularExpression("^(public|unlisted|private)$")] public string PrivacyStatus { get; init; } = "unlisted";
}
