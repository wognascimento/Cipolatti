using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SaidaAlmoxController : Controller
    {
        private readonly CipolattiContext _context;

        public SaidaAlmoxController(CipolattiContext context)
        {
            _context = context;
        }

        [HttpPost("almoxMovSaida")]
        public async Task<ActionResult> MovSaidaAlmox([FromBody] List<TSaidaAlmox> saidas)
        {
            if (saidas == null || !saidas.Any())
            {
                return BadRequest("Nenhum dado recebido para inserção.");
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                //_context.TSaidaAlmox.Add(saidas);
                var ultimoRegistro = _context.TSaidaAlmox.OrderByDescending(e => e.CodMovimentacao).FirstOrDefault();
                int novoId = (int)((ultimoRegistro != null) ? ultimoRegistro.CodMovimentacao + 1 : 1);
                foreach (var saida in saidas)
                {
                    var barcode = await _context.TblBarcodes.FirstOrDefaultAsync(b => b.Codigo == saida.Codcompladicional);
                    saida.CodMovimentacao = novoId;
                    saida.Barcode = barcode.Barcode;
                    _context.TSaidaAlmox.Add(saida);
                }
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
                return Ok("Movimentação de Saída concluída com sucesso");

            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, "Erro ao executar operação transacional");
            }

        }
    }
}
