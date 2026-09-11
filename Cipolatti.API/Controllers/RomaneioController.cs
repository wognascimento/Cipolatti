using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.Mvc;
namespace Cipolatti.API.Controllers;

[ApiController, Route("api/[controller]")]
public sealed class RomaneioController(IDbConnectionFactory db) : ControllerBase
{
    [HttpGet("romaneios")]
    public async Task<ActionResult<PagedResult<TRomaneio>>> List([FromQuery] PaginationQuery query, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM expedicao.t_romaneio", cancellationToken: ct));
        var items = (await connection.QueryAsync<TRomaneio>(new CommandDefinition("SELECT * FROM expedicao.t_romaneio ORDER BY shopping_destino LIMIT @PageSize OFFSET @Offset", new { query.PageSize, Offset = (query.Page - 1) * query.PageSize }, cancellationToken: ct))).AsList();
        return Ok(new PagedResult<TRomaneio>(items, query.Page, query.PageSize, total));
    }

    [HttpPost("romaneio")]
    public async Task<ActionResult<TRomaneio>> Insert([FromBody] TRomaneio item, CancellationToken ct)
    {
        var (sql, parameters) = DapperEntityCommands.Insert(item, true);
        await using var connection = await db.OpenConnectionAsync(ct);
        var created = await connection.QuerySingleAsync<TRomaneio>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return CreatedAtAction(nameof(Get), new { idRomaneio = created.CodRomaneiro }, created);
    }

    [HttpPut("romaneio")]
    public async Task<ActionResult> Update([FromBody] TRomaneio item, CancellationToken ct)
    {
        var (sql, parameters) = DapperEntityCommands.Update(item);
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct)) == 0 ? NotFound() : NoContent();
    }

    [HttpGet("romaneio")]
    public async Task<ActionResult<TRomaneio>> Get(int idRomaneio, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var item = await connection.QuerySingleOrDefaultAsync<TRomaneio>(new CommandDefinition("SELECT * FROM expedicao.t_romaneio WHERE cod_romaneiro = @idRomaneio", new { idRomaneio }, cancellationToken: ct));
        return item is null ? NotFound() : Ok(item);
    }
}
