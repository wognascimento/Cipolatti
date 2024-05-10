using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DescricoesController : Controller
    {
        private readonly IDescricoesRepository _descricoesRepository;

        public DescricoesController(IDescricoesRepository descricoesRepository)
        {
            _descricoesRepository = descricoesRepository;
        }

        [HttpGet("ProdutoAlmox")]
        public async Task<ActionResult<IEnumerable<QryAprovados>>> GetProdutoAlmox(int Codcompladicional)
        {
            return Ok(await _descricoesRepository.SelecionarByCodComplAdicional(Codcompladicional));
        }
    }
}
