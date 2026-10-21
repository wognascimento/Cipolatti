using System.ComponentModel.DataAnnotations;

namespace Cipolatti.API.Models.Catalogo;

public sealed record AdminTemaResumoDto(int IdTema, string Nome, string? Slug, bool Publicado,
    bool Destaque, string? Conceitos, int Pdfs, int Videos, DateTime? Atualizacao);

public sealed record AdminTemaDetalheDto(int IdTema, string NomeLegado, string? NomePublico,
    string? Slug, string? ImagemCapa, string? DescricaoCatalogo, bool Publicado, bool Destaque,
    bool Inedito, int Ordem, IReadOnlyCollection<int> ConceitoIds, IReadOnlyCollection<AdminTemaCapaDto> Capas);

public sealed record AdminTemaCapaDto(long IdTemaCapa, string ImagemUrl, int Ordem);

public sealed class AtualizarTemaCatalogoRequest
{
    [StringLength(254)] public string? NomePublico { get; init; }
    [StringLength(180)] public string? Slug { get; init; }
    [StringLength(10000)] public string? DescricaoCatalogo { get; init; }
    public bool Publicado { get; init; }
    public bool Destaque { get; init; }
    public bool Inedito { get; init; }
    [Range(0, int.MaxValue)] public int Ordem { get; init; }
    public int[] ConceitoIds { get; init; } = [];
}

public sealed class SalvarConceitoRequest
{
    [Required, StringLength(254)] public string Nome { get; init; } = "";
    [StringLength(500), Url] public string? ImagemUrl { get; init; }
}
