using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ConfCargaGeralController : Controller
    {
        private readonly IConfCargaGeral _confCargaGeralRepository;
        private readonly CipolattiContext _context;

        public ConfCargaGeralController(IConfCargaGeral confCargaGeralRepository, CipolattiContext context)
        {
            _confCargaGeralRepository = confCargaGeralRepository;
            _context = context;
        }

        [HttpPost("GravarVolume")]
        public async Task<ActionResult> CadastrarVolume(TConfCargaGeral confCarga)
        {
            var volume = await _context.TConfCargaGeral.Where(x => x.Barcode == confCarga.Barcode).FirstOrDefaultAsync();  //await _confCargaGeralRepository.SelecionarByBarcode(confCarga.Barcode);
            if (volume == null) 
            {
                _context.TConfCargaGeral.Add(confCarga); //_confCargaGeralRepository.Incluir(confCarga);
                if (await _context.SaveChangesAsync() > 0)
                {
                    return Ok("Volume enviado com sucesso!");
                }

                return BadRequest("Ocorreu um erro ao enviar o volume.");
            }
            return Ok("Nada a fazer.");
        }

        [HttpPost("GravarPreConferencia")]
        public async Task<ActionResult> CadastrarVolume(TPreConferencia preCarga)
        {
            var volume = await _context.TPreConferencia.Where(x => x.Barcode == preCarga.Barcode).FirstOrDefaultAsync();  //await _confCargaGeralRepository.SelecionarByBarcode(confCarga.Barcode);
            if (volume == null)
            {
                _context.TPreConferencia.Add(preCarga); //_confCargaGeralRepository.Incluir(confCarga);
                if (await _context.SaveChangesAsync() > 0)
                {
                    return Ok("Volume enviado com sucesso!");
                }

                return BadRequest("Ocorreu um erro ao enviar o volume.");
            }
            return Ok("Nada a fazer.");
        }
    }
}
