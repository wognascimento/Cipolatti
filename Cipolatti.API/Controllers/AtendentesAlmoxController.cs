using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AtendentesAlmoxController : Controller
    {
        private readonly IAtendentesAlmoxRepository _atendentesAlmoxRepository;

        public AtendentesAlmoxController(IAtendentesAlmoxRepository atendentesAlmoxRepository)
        {
            _atendentesAlmoxRepository = atendentesAlmoxRepository;
        }

        [HttpGet("SelecionarTodos")]
        public async Task<ActionResult<IEnumerable<Funcionarios>>> GetSelecionarTodos()
        {
            return Ok(await _atendentesAlmoxRepository.SelecionarTodos());
        }
    }
}
