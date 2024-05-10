using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AprovadoController : Controller
    {
        private readonly IAprovadoRepository _aprovadoRepository;

        public AprovadoController(IAprovadoRepository aprovadoRepository)
        {
            _aprovadoRepository = aprovadoRepository;
        }

        [HttpGet("SelecionarTodos")]
        public async Task<ActionResult<IEnumerable<QryAprovados>>> GetAprovados() 
        {
            return Ok( await _aprovadoRepository.SelecionarTodos() );
        }

        [HttpPost]
        public async Task<ActionResult> CadastrarAprovado(QryAprovados aprovado)
        {
            _aprovadoRepository.Incluir(aprovado);
            if (await _aprovadoRepository.SaveAllAsync())
            {
                return Ok("Aprovado cadastro com sucesso!");
            }

            return BadRequest("Ocorreu um erro ao salvar o aprovado.");
        }
    }
}
