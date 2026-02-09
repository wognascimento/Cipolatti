using Cipolatti.API.Models;
using Cipolatti.API.Models.DTO;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cipolatti.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VolumeControladoController : Controller
{
    //private readonly IVolumeControladoRepository _volumeControladoRepository;
    private readonly CipolattiContext _context;
    private readonly string _connectionString = "Host=localhost;Port=5432;Database=seubanco;Username=seuusuario;Password=suasenha";

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

        //var barcods = requisicoes.Select(r => r.Barcode ).ToList();
        var barcodes = requisicoes.Select(r => r.Barcode).ToArray();
        await using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            _context.ControladoShopping.AddRange(entidades);
            await _context.SaveChangesAsync();

            var req = entidades.FirstOrDefault();

            string sql = $@"
                        SELECT 
                            producao.tbl_barcodes.barcode, 
                            producao.tbl_etiqueta_zebra.codigo, 
                            producao.tbl_etiqueta_zebra.codcompladicional
                        FROM producao.tbl_barcodes 
                        INNER JOIN producao.tbl_etiqueta_zebra ON producao.tbl_barcodes.codigo = producao.tbl_etiqueta_zebra.codigo
                        WHERE producao.tbl_barcodes.barcode = ANY (@Barcodes);
                   ";


            var builder = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            IConfiguration config = builder.Build();

            string connStr = config.GetConnectionString("DefaultConnection");

            using var connection = new NpgsqlConnection(connStr);
            connection.Open();
            var produtos = connection.Query<ProdutoControladoDTO>(sql, new { Barcodes = barcodes }).ToList();

            var produtosReq = produtos
                  .GroupBy(x => new { x.codcompladicional })
                  .Select(g => new ProdutoRequisicaoControladoDTO
                  {
                      codcompladicional = g.Key.codcompladicional,
                      quantidade = g.Count(),
                  }).ToList();



            foreach (var model in produtosReq)
            {
                //await Task.Run(() => vm.BaixaReceitaAsync(item));
                
                var detReq = await _context.RequisicaoDetalhes.Where(r => r.num_requisicao == req.NumRequisicao && r.codcompladicional == model.codcompladicional).FirstOrDefaultAsync();
                if (detReq != null)
                {
                    detReq.codcompladicional = model.codcompladicional;
                    detReq.quantidade = model.quantidade;
                    detReq.data = DateTime.Now;
                    detReq.alterado_por = Environment.UserName;
                    _context.RequisicaoDetalhes.Update(detReq);
                }
                else
                {
                    var det = new DetalheRequisicaoModel
                    {
                        num_requisicao = req.NumRequisicao,
                        codcompladicional = model.codcompladicional,
                        quantidade = model.quantidade,
                        data = DateTime.Now,
                        alterado_por = Environment.UserName,
                    };
                    await _context.RequisicaoDetalhes.AddAsync(det);
                }
                await _context.SaveChangesAsync();
            }


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

    /*
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
    */
    [HttpPost("ControladosRetorno")]
    public async Task<IActionResult> PostRetorno([FromBody] List<QRCodeEntrada> controlados)
    {
        if (controlados == null || controlados.Count == 0)
            return BadRequest("Lista de controlados está vazia.");

        const int batchSize = 100;
        int inseridos = 0;

        try
        {
            for (int i = 0; i < controlados.Count; i += batchSize)
            {
                var lote = controlados
                    .Skip(i)
                    .Take(batchSize)
                    .ToList();

                foreach (var item in lote)
                {
                    var sql = @"
                    INSERT INTO producao.tbl_controlado_shopping_retorno
                        (barcode, inserido_em, inserido_por)
                    VALUES
                        (@barcode, @inserido_em, @inserido_por)
                    ON CONFLICT (barcode, inserido_em) DO NOTHING;
                ";

                    var linhas = await _context.Database.ExecuteSqlRawAsync(
                        sql,
                        new Npgsql.NpgsqlParameter("@barcode", item.Barcode),
                        new Npgsql.NpgsqlParameter("@inserido_em", item.InseridoEm),
                        new Npgsql.NpgsqlParameter("@inserido_por", item.InseridoPor)
                    );

                    inseridos += linhas; // 0 ou 1
                }
            }

            return Ok(new
            {
                sucesso = true,
                inseridos
            });
        }
        catch (TimeoutException)
        {
            return StatusCode(504, new
            {
                sucesso = false,
                erro = "Tempo excedido ao comunicar com o banco de dados."
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                sucesso = false,
                erro = "Erro interno no servidor.",
                mensagem = ex.Message
            });
        }
    }

}

