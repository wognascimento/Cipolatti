using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.Mvc;
namespace Cipolatti.API.Controllers;

[ApiController, Route("api/[controller]")]
public sealed class AtendentesAlmoxController(IDbConnectionFactory db) : ControllerBase
{
    [HttpGet("SelecionarTodos")]
    public async Task<ActionResult<IReadOnlyCollection<TblAtendentesAlmox>>> Get(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var items = await connection.QueryAsync<TblAtendentesAlmox>(new CommandDefinition("SELECT * FROM almoxarifado_jac.tbl_atendentes_almox ORDER BY nome_funcionario", cancellationToken: ct));
        return Ok(items.AsList());
    }
}
