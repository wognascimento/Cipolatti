using Cipolatti.API.Infrastructure;
using Cipolatti.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cipolatti.API.Controllers;

[ApiController, AllowAnonymous, Route("api/catalogo/capas")]
public sealed class CatalogoArquivosController(ICatalogoAdminService catalogo, ICatalogoPdfAdminService pdfs, IFileStorage storage) : ControllerBase
{
    [HttpGet("{idTema:int}")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult> Capa(int idTema, CancellationToken ct)
    {
        var path = await catalogo.ObterCapaPublicaAsync(idTema, ct);
        if (path is null) return NotFound();
        var stream = await storage.OpenReadAsync(path, ct);
        return stream is null ? NotFound() : File(stream, "image/webp", enableRangeProcessing: true);
    }

    [HttpGet("/api/catalogo/conceitos/{idConceito:int}")][ResponseCache(Duration=3600,Location=ResponseCacheLocation.Any)]
    public async Task<ActionResult> Conceito(int idConceito,CancellationToken ct){var path=await catalogo.ObterImagemConceitoAsync(idConceito,ct);if(path is null)return NotFound();var stream=await storage.OpenReadAsync(path,ct);return stream is null?NotFound():File(stream,"image/webp");}

    [HttpGet("/api/catalogo/pdfs/{idPdf:long}/paginas/{numero:int}/arquivo")]
    [ResponseCache(Duration=86400,Location=ResponseCacheLocation.Any)]
    public async Task<ActionResult> Pagina(long idPdf,int numero,CancellationToken ct)
    {var path=await pdfs.ObterPaginaPublicaAsync(idPdf,numero,ct);if(path is null)return NotFound();var stream=await storage.OpenReadAsync(path,ct);return stream is null?NotFound():File(stream,"image/webp",enableRangeProcessing:true);}

    [HttpGet("/api/catalogo/pdfs/{idPdf:long}/download")]
    public async Task<ActionResult> Download(long idPdf,CancellationToken ct)
    {var file=await pdfs.ObterDownloadPublicoAsync(idPdf,ct);if(file is null)return NotFound();var stream=await storage.OpenReadAsync(file.Value.Path,ct);return stream is null?NotFound():File(stream,"application/pdf",file.Value.FileName,enableRangeProcessing:true);}
}
