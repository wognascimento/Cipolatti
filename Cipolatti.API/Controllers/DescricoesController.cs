using Cipolatti.API.Infrastructure;
using Cipolatti.API.Models;
using Cipolatti.API.Services;
using Dapper;
using Microsoft.AspNetCore.Mvc;
namespace Cipolatti.API.Controllers;
[ApiController, Route("api/[controller]")]
public sealed class DescricoesController(IDbConnectionFactory db, IPrinterService printer) : ControllerBase
{
    [HttpGet("ProdutoAlmox")]
    public async Task<ActionResult<Qry3descricoes>> Product(int codcompladicional, CancellationToken ct)
    { var product = await Find(codcompladicional, true, ct); return product is null ? NotFound() : Ok(product); }

    [HttpPost("EtiquetaAlmox")]
    public async Task<ActionResult> Print(int codcompladicional, CancellationToken ct)
    {
        var product = await Find(codcompladicional, false, ct);
        if (product is null) return NotFound();
        await printer.PrintProductLabelAsync(product, ct);
        return Accepted(new { mensagem = "Etiqueta enviada para impressão." });
    }
    private async Task<Qry3descricoes?> Find(int code, bool almoxOnly, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        const string sql = "SELECT * FROM producao.qry3descricoes WHERE codcompladicional = @code AND (NOT @almoxOnly OR planilha ILIKE '%ALMOX%') LIMIT 1";
        return await connection.QuerySingleOrDefaultAsync<Qry3descricoes>(new CommandDefinition(sql, new { code, almoxOnly }, cancellationToken: ct));
    }
}
