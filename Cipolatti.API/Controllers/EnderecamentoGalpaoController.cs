using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.Mvc;
namespace Cipolatti.API.Controllers;

[ApiController, Route("api/[controller]")]
public sealed class EnderecamentoGalpaoController(IDbConnectionFactory db) : ControllerBase
{
    [HttpGet("Enderecos")]
    public async Task<ActionResult<PagedResult<QryEnderecamentoGalpao>>> Get([FromQuery] PaginationQuery query, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM expedicao.qry_enderecamento_galpao", cancellationToken: ct));
        var items = (await connection.QueryAsync<QryEnderecamentoGalpao>(new CommandDefinition("SELECT * FROM expedicao.qry_enderecamento_galpao ORDER BY endereco LIMIT @PageSize OFFSET @Offset", new { query.PageSize, Offset = (query.Page - 1) * query.PageSize }, cancellationToken: ct))).AsList();
        return Ok(new PagedResult<QryEnderecamentoGalpao>(items, query.Page, query.PageSize, total));
    }
}
