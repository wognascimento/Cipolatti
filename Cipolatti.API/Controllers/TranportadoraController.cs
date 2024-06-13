using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TranportadoraController : Controller
    {
        private readonly CipolattiContext _context;

        public TranportadoraController(CipolattiContext context)
        {
            _context = context;
        }

        [HttpGet("Transportadoras")]
        public async Task<ActionResult<IEnumerable<Tbltranportadoras>>> GetTransportadoras()
        {
            return Ok(await _context.Tbltranportadoras.OrderBy(x => x.Nometransportadora).ToListAsync());

        }
    }
}
