using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Cipolatti.API.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FuncionarioController : Controller
    {
        private readonly IFuncionarioRepository _funcionarioRepository;

        public FuncionarioController(IFuncionarioRepository funcionarioRepository)
        {
            _funcionarioRepository = funcionarioRepository;
        }

        [HttpGet("SelecionarByQrcode")]
        public async Task<ActionResult<IEnumerable<Funcionarios>>> GetSelecionarByQrcode(string qrcode)
        {
            return Ok(await _funcionarioRepository.SelecionarByQrcode(qrcode));
        }
    }
}
