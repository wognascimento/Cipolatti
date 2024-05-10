using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class EnderecamentoGalpaoController : Controller
    {
        private readonly IEnderecamentoGalpaoRepository _enderecamentoGalpaoRepository;

        public EnderecamentoGalpaoController(IEnderecamentoGalpaoRepository enderecamentoGalpaoRepository)
        {
            _enderecamentoGalpaoRepository = enderecamentoGalpaoRepository;
        }

        [HttpGet("Enderecos")]
        public async Task<ActionResult<IEnumerable<QryEnderecamentoGalpao>>> GetEnderecos()
        {
            return Ok(await _enderecamentoGalpaoRepository.SelecionarTodos());
        }
    }
}
