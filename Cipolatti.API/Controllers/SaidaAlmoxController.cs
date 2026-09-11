using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.Mvc;
namespace Cipolatti.API.Controllers;

[ApiController, Route("api/[controller]")]
public sealed class SaidaAlmoxController(IDbConnectionFactory db) : ControllerBase
{
    [HttpPost("almoxMovSaida")]
    public async Task<ActionResult> Insert([FromBody] List<TSaidaAlmox> saidas, CancellationToken ct)
    {
        if (saidas.Count == 0) return ValidationProblem("Informe ao menos uma saída.");
        var codigos = saidas.Where(x => x.Codcompladicional.HasValue).Select(x => x.Codcompladicional!.Value).Distinct().ToArray();
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var barcodes = (await connection.QueryAsync<(int Codigo, string Barcode)>(new CommandDefinition("SELECT codigo, barcode FROM producao.tbl_barcodes WHERE codigo = ANY(@codigos)", new { codigos }, transaction, cancellationToken: ct))).ToDictionary(x => x.Codigo, x => x.Barcode);
        var ausentes = codigos.Where(x => !barcodes.ContainsKey(x)).ToArray();
        if (ausentes.Length > 0) return BadRequest(new ProblemDetails { Title = "Barcodes não encontrados.", Extensions = { ["codigos"] = ausentes } });

        var id = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT nextval('almoxarifado_jac.t_saida_almox_movimentacao_seq')", transaction: transaction, cancellationToken: ct));
        foreach (var saida in saidas)
        {
            saida.CodMovimentacao = id;
            if (saida.Codcompladicional.HasValue) saida.Barcode = barcodes[saida.Codcompladicional.Value];
            var (sql, parameters) = DapperEntityCommands.Insert(saida);
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct));
        }
        await transaction.CommitAsync(ct);
        return Created(string.Empty, new { codMovimentacao = id, inseridos = saidas.Count });
    }
}
