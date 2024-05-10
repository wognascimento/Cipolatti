using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class VolumeControladoController : Controller
    {
        private readonly IVolumeControladoRepository _volumeControladoRepository;

        public VolumeControladoController(IVolumeControladoRepository volumeControladoRepository)
        {
            _volumeControladoRepository = volumeControladoRepository;
        }

        [HttpPost("ReceberControlado")]
        public async Task<ActionResult> ReceberControlado(TblVolumeControlado volumeControlado)
        {
            var volume = await _volumeControladoRepository.SelecionarBySiglaByVolume(volumeControlado.Sigla, volumeControlado.Volume);
            if (volume == null)
            {
                _volumeControladoRepository.Incluir(volumeControlado);
                if (await _volumeControladoRepository.SaveAllAsync())
                {
                    return Ok("Volume controlado enviado com sucesso!");
                }

                return BadRequest("Ocorreu um erro ao enviar o volume controlado.");
            }
            return Ok("Nada a fazer.");
        }
    }
}
