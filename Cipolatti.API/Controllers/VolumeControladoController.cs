using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Dapper;
using Microsoft.AspNetCore.Mvc;
namespace Cipolatti.API.Controllers;

[ApiController, Route("api/[controller]")]
public sealed class VolumeControladoController(IDbConnectionFactory db) : ControllerBase
{
    [HttpPost("ReceberControlado")]
    public async Task<ActionResult> Receive([FromBody] TblVolumeControlado item, CancellationToken ct)
    {
        const string sql = "INSERT INTO producao.tbl_volume_controlado (sigla, volume, conferido, recebido) VALUES (@Sigla, @Volume, @Conferido, @Recebido) ON CONFLICT DO NOTHING";
        await using var connection = await db.OpenConnectionAsync(ct);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(sql, item, cancellationToken: ct));
        return inserted == 0 ? Conflict(new ProblemDetails { Title = "Volume já registrado." }) : Created(string.Empty, item);
    }

    [HttpPost("ControladosRequisicao")]
    public async Task<ActionResult> Requisitions([FromBody] List<QRCodeRequisicao> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return ValidationProblem("Informe ao menos uma requisição.");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        const string insert = "INSERT INTO producao.tbl_controlado_shopping (num_requisicao, barcode, inserido_por, inserido_em) VALUES (@NumeroRequisicao, @Barcode, @InseridoPor, @InseridoEm) ON CONFLICT DO NOTHING";
        var inserted = await connection.ExecuteAsync(new CommandDefinition(insert, requests, transaction, cancellationToken: ct));

        foreach (var group in requests.GroupBy(x => x.NumeroRequisicao))
        {
            const string productsSql = "SELECT e.codcompladicional, count(*)::int AS quantidade FROM producao.tbl_barcodes b JOIN producao.tbl_etiqueta_zebra e ON e.codigo = b.codigo WHERE b.barcode = ANY(@barcodes) GROUP BY e.codcompladicional";
            var products = await connection.QueryAsync<(long Codcompladicional, int Quantidade)>(new CommandDefinition(productsSql, new { barcodes = group.Select(x => x.Barcode).ToArray() }, transaction, cancellationToken: ct));
            const string update = "UPDATE producao.t_detalhes_req SET quantidade = @Quantidade, data = CURRENT_TIMESTAMP, alterado_por = @User WHERE num_requisicao = @Request AND codcompladicional = @Codcompladicional";
            const string add = "INSERT INTO producao.t_detalhes_req (num_requisicao, codcompladicional, quantidade, data, alterado_por) VALUES (@Request, @Codcompladicional, @Quantidade, CURRENT_TIMESTAMP, @User)";
            foreach (var product in products)
            {
                var args = new { Request = group.Key, product.Codcompladicional, product.Quantidade, User = User.Identity?.Name ?? "api" };
                if (await connection.ExecuteAsync(new CommandDefinition(update, args, transaction, cancellationToken: ct)) == 0)
                    await connection.ExecuteAsync(new CommandDefinition(add, args, transaction, cancellationToken: ct));
            }
        }
        await transaction.CommitAsync(ct);
        return Created(string.Empty, new { inseridos = inserted });
    }

    [HttpPost("ControladosRetorno")]
    public async Task<ActionResult> Returns([FromBody] List<QRCodeEntrada> items, CancellationToken ct)
    {
        if (items.Count == 0) return ValidationProblem("Informe ao menos um controlado.");
        const string sql = "INSERT INTO producao.tbl_controlado_shopping_retorno (barcode, inserido_em, inserido_por) SELECT * FROM unnest(@Barcodes, @Dates, @Users) ON CONFLICT (barcode, inserido_em) DO NOTHING";
        var args = new { Barcodes = items.Select(x => x.Barcode).ToArray(), Dates = items.Select(x => x.InseridoEm).ToArray(), Users = items.Select(x => x.InseridoPor).ToArray() };
        await using var connection = await db.OpenConnectionAsync(ct);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
        return Ok(new { inseridos = inserted });
    }
}
