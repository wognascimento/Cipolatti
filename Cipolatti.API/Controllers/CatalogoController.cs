using Cipolatti.API.Models;
using Cipolatti.API.Models.Catalogo;
using Cipolatti.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cipolatti.API.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/catalogo")]
public sealed class CatalogoController(ICatalogoService catalogo) : ControllerBase
{
    [HttpGet("temas")]
    public async Task<ActionResult<PagedResult<TemaResumoDto>>> ListarTemas(
        [FromQuery] CatalogoTemasQuery query, CancellationToken ct)
    {
        if (!TryParseConceptIds(query.Conceitos, out var conceptIds, out var error))
            return ValidationProblem(error);

        var searchPattern = string.IsNullOrWhiteSpace(query.Busca) ? null : LikePattern(query.Busca);
        var filter = new CatalogoTemasFilter(searchPattern, conceptIds,
            string.Equals(query.Match, "all", StringComparison.OrdinalIgnoreCase),
            query.Destaque,
            string.IsNullOrWhiteSpace(query.FaixaPreco) ? null : query.FaixaPreco.Trim(),
            query.Page, query.PageSize);
        return Ok(await catalogo.ListarTemasAsync(filter, ct));
    }

    [HttpGet("temas/{slug}")]
    public async Task<ActionResult<TemaDetalheDto>> ObterTema(string slug, CancellationToken ct)
    {
        var item = await catalogo.ObterTemaAsync(slug, ct);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpGet("conceitos")]
    public async Task<ActionResult<IReadOnlyCollection<ConceitoDto>>> ListarConceitos(CancellationToken ct) =>
        Ok(await catalogo.ListarConceitosAsync(ct));

    [HttpGet("faixas-preco")]
    public async Task<ActionResult<IReadOnlyCollection<string>>> ListarFaixasPreco(CancellationToken ct) =>
        Ok(await catalogo.ListarFaixasPrecoAsync(ct));

    [HttpGet("temas/{slug}/pdfs")]
    public async Task<ActionResult<IReadOnlyCollection<TemaPdfDto>>> ListarPdfs(string slug, CancellationToken ct)
    {
        var items = await catalogo.ListarPdfsAsync(slug, ct);
        return items is null ? NotFound() : Ok(items);
    }

    [HttpGet("pdfs/{idPdf:long}/paginas")]
    public async Task<ActionResult<IReadOnlyCollection<TemaPdfPaginaDto>>> ListarPaginas(long idPdf, CancellationToken ct)
    {
        if (idPdf <= 0) return ValidationProblem("O identificador do PDF deve ser positivo.");
        var items = await catalogo.ListarPaginasAsync(idPdf, ct);
        return items is null ? NotFound() : Ok(items);
    }

    [HttpGet("temas/{slug}/videos")]
    public async Task<ActionResult<IReadOnlyCollection<TemaVideoDto>>> ListarVideos(string slug, CancellationToken ct)
    {
        var items = await catalogo.ListarVideosAsync(slug, ct);
        return items is null ? NotFound() : Ok(items);
    }

    [HttpGet("temas/{slug}/relacionados")]
    public async Task<ActionResult<IReadOnlyCollection<TemaResumoDto>>> ListarRelacionados(string slug, CancellationToken ct)
    {
        var items = await catalogo.ListarRelacionadosAsync(slug, ct);
        return items is null ? NotFound() : Ok(items);
    }

    internal static string LikePattern(string value) =>
        $"%{value.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";

    internal static bool TryParseConceptIds(string? value, out int[] ids, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            ids = [];
            return true;
        }

        var parsed = new HashSet<int>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var id) || id <= 0)
            {
                ids = [];
                error = "O parâmetro conceitos deve conter IDs positivos separados por vírgula.";
                return false;
            }
            parsed.Add(id);
        }
        ids = parsed.Order().ToArray();
        return true;
    }
}
