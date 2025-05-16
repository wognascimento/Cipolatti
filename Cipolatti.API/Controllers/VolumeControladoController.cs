using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System;

namespace Cipolatti.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VolumeControladoController : Controller
{
    //private readonly IVolumeControladoRepository _volumeControladoRepository;
    private readonly CipolattiContext _context;

    public VolumeControladoController(CipolattiContext context)
    {
        _context = context;
    }

    [HttpPost("ReceberControlado")]
    public async Task<ActionResult> ReceberControlado(TblVolumeControlado volumeControlado)
    {
        //var volume = await _volumeControladoRepository.SelecionarBySiglaByVolume(volumeControlado.Sigla, volumeControlado.Volume);
        var volume = await _context.TblVolumeControlado.Where(x => x.Sigla == volumeControlado.Sigla && x.Volume == volumeControlado.Volume).FirstOrDefaultAsync();
        if (volume == null)
        {
            //_volumeControladoRepository.Incluir(volumeControlado);
            _context.TblVolumeControlado.Add(volumeControlado);
            //if (await _volumeControladoRepository.SaveAllAsync())
            if (await _context.SaveChangesAsync() > 0)
            {
                return Ok("Volume controlado enviado com sucesso!");
            }

            return BadRequest("Ocorreu um erro ao enviar o volume controlado.");
        }
        return Ok("Nada a fazer.");
    }

    [HttpPost("ControladosRequisicao")]
    public async Task<IActionResult> PostRequisicoes([FromBody] List<QRCodeRequisicao> requisicoes)
    {
        if (requisicoes == null || requisicoes.Count == 0)
            return BadRequest("Lista de requisições está vazia.");

        var entidades = requisicoes.Select(r => new ControladoShoppingModel
        {
            NumRequisicao = r.NumeroRequisicao,
            Barcode = r.Barcode,
            InseridoPor = r.InseridoPor,
            InseridoEm = r.InseridoEm
        }).ToList();

        await using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            _context.ControladoShopping.AddRange(entidades);
            await _context.SaveChangesAsync();

            await transaction.CommitAsync(); // Confirma as alterações

            return Ok(new { sucesso = true, inseridos = entidades.Count });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx)
        {
            await transaction.RollbackAsync(); // Desfaz alterações em caso de erro

            return BadRequest(new
            {
                sucesso = false,
                erro = "Erro ao salvar no banco de dados.",
                codigo = pgEx.SqlState,
                mensagem = pgEx.MessageText,
                onde = pgEx.Where
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(); // Desfaz alterações genéricas

            return StatusCode(500, new
            {
                sucesso = false,
                erro = "Erro interno no servidor.",
                mensagem = ex.Message
            });
        }
    }

    [HttpPost("ControladosRetorno")]
    public async Task<IActionResult> PostRetorno([FromBody] List<QRCodeEntrada> controlados)
    {
        if (controlados == null || controlados.Count == 0)
            return BadRequest("Lista de controlados está vazia.");

        var entidades = controlados.Select(r => new ControladoShoppingRetornoModel
        {
            Barcode = r.Barcode,
            InseridoPor = r.InseridoPor,
            InseridoEm = r.InseridoEm
        }).ToList();

        await using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            _context.ControladoShoppingRetorno.AddRange(entidades);
            await _context.SaveChangesAsync();

            await transaction.CommitAsync(); // Confirma as alterações

            return Ok(new { sucesso = true, inseridos = entidades.Count });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx)
        {
            await transaction.RollbackAsync(); // Desfaz alterações em caso de erro

            return BadRequest(new
            {
                sucesso = false,
                erro = "Erro ao salvar no banco de dados.",
                codigo = pgEx.SqlState,
                mensagem = pgEx.MessageText,
                onde = pgEx.Where
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(); // Desfaz alterações genéricas

            return StatusCode(500, new
            {
                sucesso = false,
                erro = "Erro interno no servidor.",
                mensagem = ex.Message
            });
        }
    }
}

