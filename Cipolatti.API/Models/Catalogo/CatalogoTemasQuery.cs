using System.ComponentModel.DataAnnotations;

namespace Cipolatti.API.Models.Catalogo;

public sealed class CatalogoTemasQuery
{
    [StringLength(120)]
    public string? Busca { get; init; }

    [StringLength(500)]
    public string? Conceitos { get; init; }

    [RegularExpression("^(any|all)$", ErrorMessage = "Use 'any' ou 'all'.")]
    public string Match { get; init; } = "any";

    public bool? Destaque { get; init; }

    [StringLength(50)]
    public string? FaixaPreco { get; init; }

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    public int PageSize { get; init; } = 24;
}

public sealed record CatalogoTemasFilter(
    string? SearchPattern,
    int[] ConceptIds,
    bool MatchAll,
    bool? Destaque,
    string? FaixaPreco,
    int Page,
    int PageSize);
