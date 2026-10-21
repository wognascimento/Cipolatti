using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using Cipolatti.API.Services;
namespace Cipolatti.API.Controllers;

[ApiController, Route("api/[controller]")]
public sealed class RomaneioController(IDbConnectionFactory db, IRomaneioFotosService fotos) : ControllerBase
{
    [AllowAnonymous, HttpGet("romaneios")]
    public async Task<ActionResult<PagedResult<TRomaneio>>> List([FromQuery] PaginationQuery query, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM expedicao.t_romaneio", cancellationToken: ct));
        var items = (await connection.QueryAsync<TRomaneio>(new CommandDefinition("SELECT * FROM expedicao.t_romaneio ORDER BY shopping_destino LIMIT @PageSize OFFSET @Offset", new { query.PageSize, Offset = (query.Page - 1) * query.PageSize }, cancellationToken: ct))).AsList();
        return Ok(new PagedResult<TRomaneio>(items, query.Page, query.PageSize, total));
    }

    [AllowAnonymous, HttpPost("romaneio")]
    public async Task<ActionResult<TRomaneio>> Insert([FromBody] TRomaneio item, CancellationToken ct)
    {
        var (sql, parameters) = DapperEntityCommands.Insert(item, true);
        await using var connection = await db.OpenConnectionAsync(ct);
        var created = await connection.QuerySingleAsync<TRomaneio>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return CreatedAtAction(nameof(Get), new { idRomaneio = created.CodRomaneiro }, created);
    }

    [AllowAnonymous, HttpPut("romaneio")]
    public async Task<ActionResult> Update([FromBody] TRomaneio item, CancellationToken ct)
    {
        var (sql, parameters) = DapperEntityCommands.Update(item);
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct)) == 0 ? NotFound() : NoContent();
    }

    [AllowAnonymous, HttpGet("romaneio")]
    public async Task<ActionResult<TRomaneio>> Get(int idRomaneio, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var item = await connection.QuerySingleOrDefaultAsync<TRomaneio>(new CommandDefinition("SELECT * FROM expedicao.t_romaneio WHERE cod_romaneiro = @idRomaneio", new { idRomaneio }, cancellationToken: ct));
        return item is null ? NotFound() : Ok(item);
    }

    [Authorize(Policy = "CatalogoAdmin"), HttpPost("dispositivos")]
    public async Task<ActionResult<CriarDispositivoRomaneioResponse>> CreateDevice(
        [FromBody] CriarDispositivoRomaneioRequest request, CancellationToken ct)
    {
        try
        {
            var created = await fotos.CreateDeviceAsync(request.Nome, ct);
            return Created(string.Empty, created);
        }
        catch (InvalidDataException ex)
        {
            return UnprocessableEntity(new ProblemDetails { Status = 422, Title = "Dispositivo inválido.", Detail = ex.Message });
        }
    }

    [AllowAnonymous, HttpPost("dispositivos/ativar"), EnableRateLimiting("RomaneioDeviceActivation")]
    public async Task<ActionResult<AtivarDispositivoRomaneioResponse>> ActivateDevice(
        [FromBody] AtivarDispositivoRomaneioRequest request, CancellationToken ct)
    {
        var activated = await fotos.ActivateDeviceAsync(request.ActivationCode, ct);
        return activated is null
            ? Unauthorized(new ProblemDetails { Status = 401, Title = "Código de ativação inválido ou expirado." })
            : Ok(activated);
    }

    [Authorize(Policy = "CatalogoAdmin"), HttpDelete("dispositivos/{deviceId:long}")]
    public async Task<ActionResult> RevokeDevice(long deviceId, CancellationToken ct) =>
        await fotos.RevokeDeviceAsync(deviceId, ct) ? NoContent() : NotFound();

    [Authorize(AuthenticationSchemes = RomaneioDeviceDefaults.AuthenticationScheme, Policy = "RomaneioFotos")]
    [HttpPost("{codigo:int}/finalizar-fotos"), Consumes("multipart/form-data")]
    [RequestSizeLimit(160 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 160 * 1024 * 1024)]
    public async Task<ActionResult<FinalizarFotosRomaneioResponse>> FinalizePhotos(
        int codigo,
        [FromForm] FinalizarFotosRomaneioRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken ct)
    {
        if (!long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var deviceId)) return Unauthorized();
        try
        {
            return Ok(await fotos.FinalizeAsync(codigo, deviceId, idempotencyKey, request, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ProblemDetails { Status = 404, Title = "Romaneio não encontrado.", Detail = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            return UnprocessableEntity(new ProblemDetails { Status = 422, Title = "Upload inválido.", Detail = ex.Message });
        }
        catch (RomaneioEmailException ex)
        {
            return StatusCode(502, new ProblemDetails { Status = 502, Title = "Falha ao enviar a notificação.", Detail = ex.Message });
        }
    }
}
