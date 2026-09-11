using System.ComponentModel.DataAnnotations;

namespace Cipolatti.API.Models.Catalogo;

public sealed record AdminTemaPdfDto(long IdTemaPdf, int IdTema, string Titulo, string? Descricao,
    int Ordem, bool Ativo, bool Principal, bool PermitirDownload, int? QuantidadePaginas,
    string Status, string? MensagemErro, DateTime? DataAltera);

public sealed class CriarTemaPdfRequest
{
    [Required, StringLength(200)] public string Titulo { get; init; } = string.Empty;
    [StringLength(10000)] public string? Descricao { get; init; }
    public bool Principal { get; init; }
    public bool PermitirDownload { get; init; }
}

public sealed class AtualizarTemaPdfRequest
{
    [Required, StringLength(200)] public string Titulo { get; init; } = string.Empty;
    [StringLength(10000)] public string? Descricao { get; init; }
    [Range(0, int.MaxValue)] public int Ordem { get; init; }
    public bool Ativo { get; init; } = true;
    public bool Principal { get; init; }
    public bool PermitirDownload { get; init; }
}

public sealed record ReordenarTemaPdfItem(long IdTemaPdf, int Ordem);
