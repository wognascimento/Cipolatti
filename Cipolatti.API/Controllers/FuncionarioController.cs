using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.Mvc;
namespace Cipolatti.API.Controllers;

[ApiController, Route("api/[controller]")]
public sealed class FuncionarioController(IDbConnectionFactory db) : ControllerBase
{
    [HttpGet("SelecionarByQrcode")]
    public async Task<ActionResult<Funcionarios>> Get([FromQuery] string qrcode, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var item = await connection.QuerySingleOrDefaultAsync<Funcionarios>(new CommandDefinition("SELECT * FROM ht.funcionarios WHERE qrcode = @qrcode LIMIT 1", new { qrcode }, cancellationToken: ct));
        return item is null ? NotFound() : Ok(item);
    }
}
