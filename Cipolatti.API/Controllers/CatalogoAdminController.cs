using Cipolatti.API.Models;
using Cipolatti.API.Models.Catalogo;
using Cipolatti.API.Infrastructure;
using Cipolatti.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cipolatti.API.Controllers;

[ApiController, Authorize(Policy = "CatalogoAdmin"), Route("api/catalogo/admin")]
public sealed class CatalogoAdminController(ICatalogoAdminService catalogo, ICatalogoPdfAdminService pdfs,ICatalogoVideoAdminService videos,IYoutubeService youtube) : ControllerBase
{
    [HttpGet("conceitos")]
    public async Task<ActionResult<IReadOnlyCollection<ConceitoDto>>> Conceitos(CancellationToken ct) =>
        Ok(await catalogo.ListarConceitosAsync(ct));

    [HttpPost("conceitos")]
    public async Task<ActionResult<ConceitoDto>> CriarConceito(SalvarConceitoRequest request,CancellationToken ct)
    {try{var item=await catalogo.CriarConceitoAsync(request,User.Identity?.Name,ct);return Created($"api/catalogo/admin/conceitos/{item.IdConceito}",item);}catch(CatalogConflictException ex){return Conflict(new ProblemDetails{Status=409,Title="Conceito duplicado.",Detail=ex.Message});}catch(ArgumentException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="Conceito invalido.",Detail=ex.Message});}}

    [HttpPut("conceitos/{idConceito:int}")]
    public async Task<ActionResult<ConceitoDto>> AtualizarConceito(int idConceito,SalvarConceitoRequest request,CancellationToken ct)
    {try{var item=await catalogo.AtualizarConceitoAsync(idConceito,request,User.Identity?.Name,ct);return item is null?NotFound():Ok(item);}catch(CatalogConflictException ex){return Conflict(new ProblemDetails{Status=409,Title="Conceito duplicado.",Detail=ex.Message});}catch(ArgumentException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="Conceito invalido.",Detail=ex.Message});}}

    [HttpDelete("conceitos/{idConceito:int}")]
    public async Task<ActionResult> ExcluirConceito(int idConceito,[FromQuery] bool desassociar=false,CancellationToken ct=default)
    {
        try { return await catalogo.ExcluirConceitoAsync(idConceito,desassociar,User.Identity?.Name,ct)?NoContent():NotFound(); }
        catch(InvalidOperationException ex) { return Conflict(new ProblemDetails{Status=409,Title="Conceito possui temas associados.",Detail=ex.Message}); }
    }

    [HttpPost("conceitos/{idConceito:int}/imagem")][RequestSizeLimit(20*1024*1024)]
    public async Task<ActionResult> AtualizarImagemConceito(int idConceito,IFormFile arquivo,CancellationToken ct){if(arquivo.Length==0)return ValidationProblem("Informe uma imagem.");try{await using var stream=arquivo.OpenReadStream();var ok=await catalogo.AtualizarImagemConceitoAsync(idConceito,stream,arquivo.ContentType,User.Identity?.Name,ct);return ok?Ok(new{imagemUrl=$"/api/catalogo/conceitos/{idConceito}"}):NotFound();}catch(InvalidDataException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="Imagem inválida.",Detail=ex.Message});}}

    [HttpGet("temas")]
    public async Task<ActionResult<PagedResult<AdminTemaResumoDto>>> Listar([FromQuery] string? busca,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 100) return ValidationProblem("Paginação inválida.");
        return Ok(await catalogo.ListarAsync(busca, page, pageSize, ct));
    }

    [HttpGet("temas/{idTema:int}")]
    public async Task<ActionResult<AdminTemaDetalheDto>> Obter(int idTema, CancellationToken ct)
    { var item=await catalogo.ObterAsync(idTema,ct); return item is null?NotFound():Ok(item); }

    [HttpPut("temas/{idTema:int}")]
    public async Task<ActionResult<AdminTemaDetalheDto>> Atualizar(int idTema, AtualizarTemaCatalogoRequest request, CancellationToken ct)
    {
        try { var item=await catalogo.AtualizarAsync(idTema,request,User.Identity?.Name,ct); return item is null?NotFound():Ok(item); }
        catch(ArgumentException ex) { return UnprocessableEntity(new ProblemDetails{Status=422,Title="Dados do tema inválidos.",Detail=ex.Message}); }
    }

    [HttpPost("temas/{idTema:int}/capa")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<ActionResult> AtualizarCapa(int idTema, IFormFile arquivo, CancellationToken ct)
    {
        if(arquivo.Length==0) return ValidationProblem("Informe uma imagem.");
        try
        {
            await using var stream=arquivo.OpenReadStream();
            var path=await catalogo.AtualizarCapaAsync(idTema,stream,arquivo.ContentType,User.Identity?.Name,ct);
            return path is null?NotFound():Ok(new{imagemCapa=$"/api/catalogo/capas/{idTema}"});
        }
        catch(InvalidDataException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="Imagem inválida.",Detail=ex.Message});}
    }

    [HttpPost("temas/{idTema:int}/capas")]
    [RequestSizeLimit(200L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit=200L * 1024 * 1024)]
    public async Task<ActionResult<IReadOnlyCollection<AdminTemaCapaDto>>> AdicionarCapas(int idTema, List<IFormFile> arquivos, CancellationToken ct)
    {
        if (arquivos.Count == 0 || arquivos.Any(x => x.Length == 0)) return ValidationProblem("Informe ao menos uma imagem.");
        var streams = arquivos.Select(x => ((Stream)x.OpenReadStream(), x.ContentType)).ToArray();
        try { var items = await catalogo.AdicionarCapasAsync(idTema, streams, User.Identity?.Name, ct); return items is null ? NotFound() : Ok(items); }
        catch (InvalidDataException ex) { return UnprocessableEntity(new ProblemDetails { Status=422, Title="Imagem inválida.", Detail=ex.Message }); }
        finally { foreach (var item in streams) await item.Item1.DisposeAsync(); }
    }

    [HttpDelete("capas/{idTemaCapa:long}")]
    public async Task<ActionResult> RemoverCapa(long idTemaCapa, CancellationToken ct) =>
        await catalogo.RemoverCapaAsync(idTemaCapa, User.Identity?.Name, ct) ? NoContent() : NotFound();

    [HttpGet("temas/{idTema:int}/pdfs")]
    public async Task<ActionResult<IReadOnlyCollection<AdminTemaPdfDto>>> ListarPdfs(int idTema,CancellationToken ct)
    {var items=await pdfs.ListarAsync(idTema,ct);return items is null?NotFound():Ok(items);}

    [HttpPost("temas/{idTema:int}/pdfs")]
    [RequestSizeLimit(150L*1024*1024)]
    public async Task<ActionResult<AdminTemaPdfDto>> CriarPdf(int idTema,[FromForm] CriarTemaPdfRequest request,IFormFile arquivo,CancellationToken ct)
    {
        if(arquivo.Length==0)return ValidationProblem("Informe um PDF.");
        try{await using var stream=arquivo.OpenReadStream();var item=await pdfs.CriarAsync(idTema,request,stream,arquivo.ContentType,User.Identity?.Name,ct);return item is null?NotFound():Accepted(item);}
        catch(InvalidDataException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="PDF invalido.",Detail=ex.Message});}
        catch(ArgumentException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="Dados invalidos.",Detail=ex.Message});}
        catch(CatalogConflictException ex){return Conflict(new ProblemDetails{Status=409,Title="PDF duplicado.",Detail=ex.Message});}
    }

    [HttpPut("pdfs/{idPdf:long}")]
    public async Task<ActionResult<AdminTemaPdfDto>> AtualizarPdf(long idPdf,AtualizarTemaPdfRequest request,CancellationToken ct)
    {try{var item=await pdfs.AtualizarAsync(idPdf,request,User.Identity?.Name,ct);return item is null?NotFound():Ok(item);}catch(ArgumentException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="Dados invalidos.",Detail=ex.Message});}}

    [HttpPost("pdfs/{idPdf:long}/substituir")]
    [RequestSizeLimit(150L*1024*1024)]
    public async Task<ActionResult> SubstituirPdf(long idPdf,IFormFile arquivo,CancellationToken ct)
    {try{await using var stream=arquivo.OpenReadStream();return await pdfs.SubstituirAsync(idPdf,stream,arquivo.ContentType,User.Identity?.Name,ct)?Accepted():NotFound();}catch(InvalidDataException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="PDF invalido.",Detail=ex.Message});}}

    [HttpPost("pdfs/{idPdf:long}/reprocessar")]
    public async Task<ActionResult> ReprocessarPdf(long idPdf,CancellationToken ct)=>await pdfs.ReprocessarAsync(idPdf,User.Identity?.Name,ct)?Accepted():NotFound();

    [HttpPost("pdfs/{idPdf:long}/adicionar-segunda-pagina-como-capa")]
    public async Task<ActionResult> AdicionarSegundaPaginaComoCapa(long idPdf,CancellationToken ct)
    {
        try { return await pdfs.AdicionarSegundaPaginaComoCapaAsync(idPdf,User.Identity?.Name,ct)?Ok(new { mensagem="A segunda página foi adicionada às capas." }):NotFound(); }
        catch (InvalidOperationException ex) { return UnprocessableEntity(new ProblemDetails { Status=422, Title="A capa ainda não está disponível.", Detail=ex.Message }); }
        catch (FileNotFoundException ex) { return UnprocessableEntity(new ProblemDetails { Status=422, Title="A capa ainda não está disponível.", Detail=ex.Message }); }
    }

    [HttpDelete("pdfs/{idPdf:long}")]
    public async Task<ActionResult> DesativarPdf(long idPdf,CancellationToken ct)=>await pdfs.DesativarAsync(idPdf,User.Identity?.Name,ct)?NoContent():NotFound();

    [HttpPost("pdfs/reordenar")]
    public async Task<ActionResult> ReordenarPdfs(IReadOnlyCollection<ReordenarTemaPdfItem> items,CancellationToken ct)
    {try{return await pdfs.ReordenarAsync(items,User.Identity?.Name,ct)?NoContent():NotFound();}catch(ArgumentException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="Ordenacao invalida.",Detail=ex.Message});}}

    [HttpGet("temas/{idTema:int}/videos")]
    public async Task<ActionResult<IReadOnlyCollection<AdminTemaVideoDto>>> ListarVideos(int idTema,CancellationToken ct)
    {var items=await videos.ListarAsync(idTema,ct);return items is null?NotFound():Ok(items);}

    [HttpPost("temas/{idTema:int}/videos")]
    public async Task<ActionResult<AdminTemaVideoDto>> CriarVideo(int idTema,CriarTemaVideoRequest request,CancellationToken ct)
    {try{var item=await videos.CriarAsync(idTema,request,User.Identity?.Name,ct);return item is null?NotFound():CreatedAtAction(nameof(ListarVideos),new{idTema},item);}catch(CatalogConflictException ex){return Conflict(new ProblemDetails{Status=409,Title="Video duplicado.",Detail=ex.Message});}catch(ArgumentException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="Video invalido.",Detail=ex.Message});}}

    [HttpPost("temas/{idTema:int}/videos/upload")]
    [RequestSizeLimit(1030L*1024*1024)]
    [RequestFormLimits(MultipartBodyLengthLimit=1030L*1024*1024)]
    public async Task<ActionResult<AdminTemaVideoDto>> UploadVideo(int idTema,[FromForm] YoutubeUploadRequest request,IFormFile arquivo,IFormFile? thumbnail,CancellationToken ct)
    {
        if(arquivo is null||arquivo.Length==0)return ValidationProblem("Informe o arquivo de vídeo.");
        try{await using var stream=arquivo.OpenReadStream();await using var thumb=thumbnail?.OpenReadStream();var item=await youtube.UploadVideoAsync(idTema,request,stream,arquivo.ContentType,arquivo.Length,thumb,thumbnail?.ContentType,User.Identity?.Name,ct);return CreatedAtAction(nameof(ListarVideos),new{idTema},item);}
        catch(KeyNotFoundException ex){return NotFound(new ProblemDetails{Status=404,Title="Tema não encontrado.",Detail=ex.Message});}
        catch(ArgumentException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="Upload inválido.",Detail=ex.Message});}
        catch(InvalidOperationException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="Não foi possível publicar no YouTube.",Detail=ex.Message});}
    }

    [HttpPut("videos/{idVideo:long}")]
    public async Task<ActionResult<AdminTemaVideoDto>> AtualizarVideo(long idVideo,AtualizarTemaVideoRequest request,CancellationToken ct)
    {try{var item=await videos.AtualizarAsync(idVideo,request,User.Identity?.Name,ct);return item is null?NotFound():Ok(item);}catch(CatalogConflictException ex){return Conflict(new ProblemDetails{Status=409,Title="Video duplicado.",Detail=ex.Message});}catch(ArgumentException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="Video invalido.",Detail=ex.Message});}}

    [HttpDelete("videos/{idVideo:long}")]
    public async Task<ActionResult> DesativarVideo(long idVideo,CancellationToken ct)=>await videos.DesativarAsync(idVideo,User.Identity?.Name,ct)?NoContent():NotFound();

    [HttpPost("videos/reordenar")]
    public async Task<ActionResult> ReordenarVideos(IReadOnlyCollection<ReordenarTemaVideoItem> items,CancellationToken ct)
    {try{return await videos.ReordenarAsync(items,User.Identity?.Name,ct)?NoContent():NotFound();}catch(ArgumentException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="Ordenacao invalida.",Detail=ex.Message});}}

    [HttpGet("youtube/status")]
    public async Task<ActionResult<YoutubeStatusDto>> YoutubeStatus(CancellationToken ct)=>Ok(await youtube.GetStatusAsync(ct));

    [HttpGet("youtube/connect")]
    public ActionResult<YoutubeConnectDto> YoutubeConnect()
    {try{return Ok(new YoutubeConnectDto(youtube.CreateAuthorizationUrl(User.Identity?.Name)));}catch(InvalidOperationException ex){return UnprocessableEntity(new ProblemDetails{Status=422,Title="YouTube nao configurado.",Detail=ex.Message});}}

    [AllowAnonymous,HttpGet("youtube/callback")]
    public async Task<ActionResult> YoutubeCallback([FromQuery]string? code,[FromQuery]string? state,[FromQuery]string? error,CancellationToken ct)
    {
        if(!string.IsNullOrWhiteSpace(error))return BadRequest(new ProblemDetails{Status=400,Title="Autorizacao YouTube recusada.",Detail=error});
        if(string.IsNullOrWhiteSpace(code)||string.IsNullOrWhiteSpace(state))return BadRequest(new ProblemDetails{Status=400,Title="Callback OAuth invalido."});
        try{return Redirect(await youtube.CompleteAuthorizationAsync(code,state,ct));}
        catch(ArgumentException ex){return BadRequest(new ProblemDetails{Status=400,Title="Callback OAuth invalido.",Detail=ex.Message});}
    }
}
