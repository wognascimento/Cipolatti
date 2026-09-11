using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.Mvc;
namespace Cipolatti.API.Controllers;

[ApiController, Route("api/[controller]")]
public sealed class TranportadoraController(IDbConnectionFactory db) : ControllerBase
{
    [HttpGet("Transportadoras")]
    public async Task<ActionResult<PagedResult<Tbltranportadoras>>> Get([FromQuery] PaginationQuery query, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM operacional.tbltranportadoras", cancellationToken: ct));
        var items = (await connection.QueryAsync<Tbltranportadoras>(new CommandDefinition("SELECT * FROM operacional.tbltranportadoras ORDER BY nometransportadora LIMIT @PageSize OFFSET @Offset", new { query.PageSize, Offset = (query.Page - 1) * query.PageSize }, cancellationToken: ct))).AsList();
        return Ok(new PagedResult<Tbltranportadoras>(items, query.Page, query.PageSize, total));
    }
}
