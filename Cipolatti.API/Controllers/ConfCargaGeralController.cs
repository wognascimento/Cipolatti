using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ConfCargaGeralController : Controller
    {
        private readonly IConfCargaGeral _confCargaGeralRepository;

        public ConfCargaGeralController(IConfCargaGeral confCargaGeralRepository)
        {
            _confCargaGeralRepository = confCargaGeralRepository;
        }

        [HttpPost("GravarVolume")]
        public async Task<ActionResult> CadastrarVolume(TConfCargaGeral confCarga)
        {
            var volume = await _confCargaGeralRepository.SelecionarByBarcode(confCarga.Barcode);
            if (volume == null) 
            {
                _confCargaGeralRepository.Incluir(confCarga);
                if (await _confCargaGeralRepository.SaveAllAsync())
                {
                    return Ok("Volume enviado com sucesso!");
                }

                return BadRequest("Ocorreu um erro ao enviar o volume.");
            }
            return Ok("Nada a fazer.");
        }
    }
}
