namespace Cipolatti.API.Models.Catalogo;

public sealed record ConceitoDto(int IdConceito, string Nome, int QuantidadeTemas = 0, string? ImagemUrl = null);

public sealed record TemaResumoDto(
    int IdTema,
    string Slug,
    string Nome,
    string? ImagemCapa,
    string? Descricao,
    bool Destaque,
    bool Inedito,
    int Ordem,
    IReadOnlyCollection<ConceitoDto> Conceitos);

public sealed record TemaDetalheDto(
    int IdTema,
    string Slug,
    string Nome,
    string? ImagemCapa,
    string? Descricao,
    bool Destaque,
    bool Inedito,
    IReadOnlyCollection<ConceitoDto> Conceitos,
    IReadOnlyCollection<TemaPdfDto> Pdfs,
    IReadOnlyCollection<TemaVideoDto> Videos);

public sealed record TemaPdfDto(
    long IdTemaPdf,
    string Titulo,
    string? Descricao,
    int Ordem,
    bool Principal,
    bool PermitirDownload,
    int? QuantidadePaginas);

public sealed record TemaPdfPaginaDto(
    long IdTemaPdfPagina,
    int NumeroPagina,
    string ArquivoImagem,
    int? Largura,
    int? Altura,
    long? TamanhoBytes);

public sealed record TemaVideoDto(
    long IdTemaVideo,
    string Titulo,
    string? Descricao,
    string YoutubeVideoId,
    string? YoutubeUrl,
    string? ThumbnailUrl,
    int Ordem,
    string? PrivacyStatus);
