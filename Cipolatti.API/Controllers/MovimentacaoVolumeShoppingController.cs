using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Cipolatti.API.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.ObjectModel;

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
        //public async Task<ActionResult> CadastrarVolume(TblMovimentacaoVolumeShopping endercoVolume)
        public async Task<ActionResult> CadastrarVolume([FromBody] ObservableCollection<TblMovimentacaoVolumeShopping> volumes)
        {

            if (volumes == null || volumes.Count == 0)
            {
                return BadRequest("Nenhum dado recebido para inserção.");
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                //_context.TSaidaAlmox.Add(saidas);
                //var ultimoRegistro = _context.TSaidaAlmox.OrderByDescending(e => e.CodMovimentacao).FirstOrDefault();
                //int novoId = (int)((ultimoRegistro != null) ? ultimoRegistro.CodMovimentacao + 1 : 1);
                foreach (var volume in volumes)
                {
                    _context.TblMovimentacaoVolumeShopping.Add(volume);
                }
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
                return Ok("Volume(s) endereçado(s) com sucesso!");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, "Erro ao executar operação transacional");
            }
            /*
            var volume = await _context.TblMovimentacaoVolumeShopping.Where(x => x.BarcodeVolume == endercoVolume.BarcodeVolume).FirstOrDefaultAsync();
            if (volume == null)
            {
                _context.TblMovimentacaoVolumeShopping.Add(endercoVolume);
                bool save = await _context.SaveChangesAsync() > 0;
                if (save)
                {
                    return Ok("Volume enviado com sucesso!");
                }

                return BadRequest("Ocorreu um erro ao enviar o volume.");
            }
            return Ok("Nada a fazer.");
            */
        }

        [HttpGet("Endereco")]
        public async Task<ActionResult<IEnumerable<QryEnderecamentoGalpao>>> GetEndereco(string Barcode)
        {
            return Ok(await _context.QryEnderecamentoGalpao.Where(x => x.Barcode == Barcode).FirstOrDefaultAsync());
        }

        [HttpGet("Volume")]
        public async Task<ActionResult<IEnumerable<QryLookup>>> GetVolume(string qrcode)
        {
            return Ok(await _context.QryLookup.Where(x => x.Qrcode.Contains(qrcode)).FirstOrDefaultAsync());
        }


    }
}
