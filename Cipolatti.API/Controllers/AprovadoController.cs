using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Cipolatti.API.Controllers;

[ApiController, Route("api/[controller]")]
public sealed class AprovadoController(IDbConnectionFactory db) : ControllerBase
{
    [AllowAnonymous, HttpGet("SelecionarTodos")]
    public async Task<ActionResult<PagedResult<QryAprovados>>> Get([FromQuery] PaginationQuery query, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        const string countSql = "SELECT count(*) FROM producao.qry_aprovados";
        const string sql = "SELECT * FROM producao.qry_aprovados ORDER BY id_aprovado NULLS LAST LIMIT @PageSize OFFSET @Offset";
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, cancellationToken: ct));
        var items = (await connection.QueryAsync<QryAprovados>(new CommandDefinition(sql, new { query.PageSize, Offset = (query.Page - 1) * query.PageSize }, cancellationToken: ct))).AsList();
        return Ok(new PagedResult<QryAprovados>(items, query.Page, query.PageSize, total));
    }
}
