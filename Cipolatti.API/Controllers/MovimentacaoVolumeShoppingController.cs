using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Cipolatti.API.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MovimentacaoVolumeShoppingController : Controller
    {
        private readonly IMovimentacaoVolumeShoppingRepository _movimentacaoVolumeShoppingRepository;
        private readonly CipolattiContext _context;

        /*
        public MovimentacaoVolumeShoppingController(IMovimentacaoVolumeShoppingRepository movimentacaoVolumeShoppingRepository)
        {
            _movimentacaoVolumeShoppingRepository = movimentacaoVolumeShoppingRepository;
        }
        */

        public MovimentacaoVolumeShoppingController(CipolattiContext context)
        {
            _context = context;
        }

        [HttpPost("GravarVolume")]
        public async Task<ActionResult> CadastrarVolume(TblMovimentacaoVolumeShopping endercoVolume)
        {
            //var volume = await _movimentacaoVolumeShoppingRepository.SelecionarByBarcode(endercoVolume.BarcodeVolume);
            var volume = await _context.TblMovimentacaoVolumeShopping.Where(x => x.BarcodeVolume == endercoVolume.BarcodeVolume).FirstOrDefaultAsync();
            if (volume == null)
            {
                //_movimentacaoVolumeShoppingRepository.Incluir(endercoVolume);
                _context.TblMovimentacaoVolumeShopping.Add(endercoVolume);
                bool save = await _context.SaveChangesAsync() > 0;
                if (save)
                {
                    return Ok("Volume enviado com sucesso!");
                }

                return BadRequest("Ocorreu um erro ao enviar o volume.");
            }
            return Ok("Nada a fazer.");
        }

        [HttpGet("Endereco")]
        public async Task<ActionResult<IEnumerable<QryEnderecamentoGalpao>>> GetEndereco(string Barcode)
        {
            return Ok(await _context.QryEnderecamentoGalpao.Where(x => x.Barcode == Barcode).FirstOrDefaultAsync());
        }

        [HttpGet("Volume")]
        public async Task<ActionResult<IEnumerable<QryLookup>>> GetVolume(string qrcode)
        {
            return Ok(await _context.QryLookup.Where(x => x.Qrcode == qrcode).FirstOrDefaultAsync());
        }


    }
}
