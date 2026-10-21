using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Cipolatti.API.Controllers;

[ApiController, Route("api/[controller]")]
public sealed class MovimentacaoVolumeShoppingController(IDbConnectionFactory db) : ControllerBase
{
    [AllowAnonymous, HttpPost("GravarVolume")]
    public async Task<ActionResult> Insert([FromBody] List<TblMovimentacaoVolumeShopping> volumes, CancellationToken ct)
    {
        if (volumes.Count == 0) return ValidationProblem("Informe ao menos um volume.");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var volume in volumes)
        {
            var (sql, parameters) = DapperEntityCommands.Insert(volume);
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct));
        }
        await transaction.CommitAsync(ct);
        return Created(string.Empty, new { inseridos = volumes.Count });
    }

    [AllowAnonymous, HttpGet("Endereco")]
    public async Task<ActionResult<QryEnderecamentoGalpao>> Endereco(string barcode, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var item = await connection.QuerySingleOrDefaultAsync<QryEnderecamentoGalpao>(new CommandDefinition("SELECT * FROM expedicao.qry_enderecamento_galpao WHERE barcode = @barcode LIMIT 1", new { barcode }, cancellationToken: ct));
        return item is null ? NotFound() : Ok(item);
    }

    [AllowAnonymous, HttpGet("Volume")]
    public async Task<ActionResult<QryLookup>> Volume(string qrcode, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var item = await connection.QuerySingleOrDefaultAsync<QryLookup>(new CommandDefinition("SELECT * FROM expedicao.qry_lookup WHERE qrcode = @qrcode LIMIT 1", new { qrcode }, cancellationToken: ct));
        return item is null ? NotFound() : Ok(item);
    }
}
