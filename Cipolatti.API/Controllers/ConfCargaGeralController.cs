using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Cipolatti.API.Controllers;

[ApiController, Route("api/[controller]")]
public sealed class ConfCargaGeralController(IDbConnectionFactory db) : ControllerBase
{
    [AllowAnonymous, HttpPost("GravarVolume")]
    public Task<ActionResult> Volume([FromBody] TConfCargaGeral item, CancellationToken ct) =>
        Insert(item, "expedicao.t_conf_carga_geral", ct);

    [AllowAnonymous, HttpPost("GravarPreConferencia")]
    public Task<ActionResult> PreConferencia([FromBody] TPreConferencia item, CancellationToken ct) =>
        Insert(item, "expedicao.t_pre_conferencia", ct);

    private async Task<ActionResult> Insert<T>(T item, string table, CancellationToken ct)
    {
        var (sql, parameters) = DapperEntityCommands.Insert(item);
        sql += " ON CONFLICT (barcode) DO NOTHING";
        await using var connection = await db.OpenConnectionAsync(ct);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return inserted == 0 ? Conflict(new ProblemDetails { Title = "Volume já registrado." }) : Created(string.Empty, item);
    }
}
