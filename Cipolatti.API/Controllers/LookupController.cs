using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.Mvc;
namespace Cipolatti.API.Controllers;

[ApiController, Route("api/[controller]")]
public sealed class LookupController(IDbConnectionFactory db) : ControllerBase
{
    [HttpGet("LookupBySigla")]
    public Task<ActionResult<IReadOnlyCollection<QryLookup>>> BySigla(string sigla, CancellationToken ct) => Query(sigla, null, ct);

    [HttpGet("lookupBySiglaByCaminhao")]
    public Task<ActionResult<IReadOnlyCollection<QryLookup>>> ByCaminhao(string sigla, string caminhao, CancellationToken ct) => Query(sigla, caminhao, ct);

    private async Task<ActionResult<IReadOnlyCollection<QryLookup>>> Query(string sigla, string? caminhao, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        const string sql = "SELECT * FROM expedicao.qry_lookup WHERE sigla_serv = @sigla AND (@caminhao IS NULL OR baia_caminhao = @caminhao)";
        var items = await connection.QueryAsync<QryLookup>(new CommandDefinition(sql, new { sigla, caminhao }, cancellationToken: ct));
        return Ok(items.AsList());
    }
}
