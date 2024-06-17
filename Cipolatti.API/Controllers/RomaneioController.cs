using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RomaneioController : Controller
    {
        private readonly CipolattiContext _context;

        public RomaneioController(CipolattiContext context)
        {
            _context = context;
        }

        [HttpGet("romaneios")]
        public async Task<ActionResult<IEnumerable<TRomaneio>>> GetRomaneios()
        {
            return Ok(await _context.TRomaneio.OrderBy(x => x.ShoppingDestino).ToListAsync());

        }

        [HttpPost("romaneio")]
        public async Task<ActionResult> CadastrarRomaneio([FromBody] TRomaneio romaneio)
        {
            if (romaneio == null)
                return BadRequest("Nenhum dado recebido para inserção.");

            try
            {
                await _context.TRomaneio.AddAsync(romaneio);
                await _context.SaveChangesAsync();
                return Ok(romaneio);
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Erro ao gravar romaneio");
            }
        }

        [HttpPut("romaneio")]
        public async Task<ActionResult> AtualizarRomaneio([FromBody] TRomaneio romaneio)
        {
            try
            {
                var r = await _context.TRomaneio.FindAsync(romaneio.CodRomaneiro);
                _context.TRomaneio.Update(r);
                await _context.SaveChangesAsync();
                return Ok("Romaneio salvo com sucesso!");
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Erro ao gravar romaneio");
            }
        }

    }
}
