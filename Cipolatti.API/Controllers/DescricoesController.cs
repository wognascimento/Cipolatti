using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Sockets;
using TscZebra.Plugin.Abstractions.Enums;
using TscZebra.Plugin.Abstractions;
using TscZebra.Plugin;
using TscZebra.Plugin.Abstractions.Exceptions;
using System.IO;
using System.Runtime.InteropServices;


public class TSCLIB_DLL
{
    [DllImport("TSCLIB.dll", EntryPoint = "about")]
    public static extern int about();

    [DllImport("TSCLIB.dll", EntryPoint = "openport")]
    public static extern int openport(string printername);

    [DllImport("TSCLIB.dll", EntryPoint = "barcode")]
    public static extern int barcode(string x, string y, string type,
                string height, string readable, string rotation,
                string narrow, string wide, string code);

    [DllImport("TSCLIB.dll", EntryPoint = "clearbuffer")]
    public static extern int clearbuffer();

    [DllImport("TSCLIB.dll", EntryPoint = "closeport")]
    public static extern int closeport();

    [DllImport("TSCLIB.dll", EntryPoint = "downloadpcx")]
    public static extern int downloadpcx(string filename, string image_name);

    [DllImport("TSCLIB.dll", EntryPoint = "formfeed")]
    public static extern int formfeed();

    [DllImport("TSCLIB.dll", EntryPoint = "nobackfeed")]
    public static extern int nobackfeed();

    [DllImport("TSCLIB.dll", EntryPoint = "printerfont")]
    public static extern int printerfont(string x, string y, string fonttype,
                    string rotation, string xmul, string ymul,
                    string text);

    [DllImport("TSCLIB.dll", EntryPoint = "printlabel")]
    public static extern int printlabel(string set, string copy);

    [DllImport("TSCLIB.dll", EntryPoint = "sendcommand")]
    public static extern int sendcommand(string printercommand);

    [DllImport("TSCLIB.dll", EntryPoint = "setup")]
    public static extern int setup(string width, string height,
              string speed, string density,
              string sensor, string vertical,
              string offset);

    [DllImport("TSCLIB.dll", EntryPoint = "windowsfont")]
    public static extern int windowsfont(int x, int y, int fontheight,
                    int rotation, int fontstyle, int fontunderline,
                    string szFaceName, string content);

}
namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DescricoesController : Controller
    {
        private readonly IDescricoesRepository _descricoesRepository;
        private readonly CipolattiContext _context;

        public DescricoesController(IDescricoesRepository descricoesRepository, CipolattiContext context)
        {
            _descricoesRepository = descricoesRepository;
            _context = context;
        }

        [HttpGet("ProdutoAlmox")]
        public async Task<ActionResult<IEnumerable<Qry3descricoes>>> GetProdutoAlmox(int Codcompladicional)
        {
            return Ok(await _descricoesRepository.SelecionarByCodComplAdicional(Codcompladicional));
        }

        [HttpGet("EtiquetaAlmox")]
        public async Task<ActionResult> CadastrarVolume(int Codcompladicional)
        {
            var descricao =  await _context.Qry3descricoes.Where(x => x.Codcompladicional == Codcompladicional).FirstOrDefaultAsync();
            if (descricao != null)
            {

                /*
                TcpClient? client = new();
                await client.ConnectAsync("192.168.0.118", 9100);
                StreamWriter streamWriter1 = new(client.GetStream());

                streamWriter1.WriteLine($@"^XA");
                streamWriter1.WriteLine($@"^FT147,267^BQN,2,10");
                streamWriter1.WriteLine($@"^FH\^FDLA,123456789012^FS");
                streamWriter1.WriteLine($@"^FO87,48^GB34,168,34^FS");
                streamWriter1.WriteLine($@"^FT93,48^A0R,28,28^FB168,1,0,C^FR^FH\^FD123456789012^FS");
                streamWriter1.WriteLine($@"^FT293,247^A0R,28,28^FH\^FDRELE TEMPORIZADOR CONTA^FS");
                streamWriter1.WriteLine($@"^FT266,247^A0R,28,28^FH\^FDRELE TEMPORIZADOR CONTA^FS");
                streamWriter1.WriteLine($@"^FT238,247^A0R,28,28^FH\^FDRELE TEMPORIZADOR CONTA^FS");
                streamWriter1.WriteLine($@"^FT211,247^A0R,28,28^FH\^FDRELE TEMPORIZADOR CONTA^FS");
                streamWriter1.WriteLine($@"^FT183,247^A0R,28,28^FH\^FDRELE TEMPORIZADOR CONTA^FS");
                streamWriter1.WriteLine($@"^PQ1,0,1,Y^XZ");
                */

                // ZPL Command(s)
                string ZPLString =
                 "^XA" +
                 "^FO50,50" +
                 "^A0N50,50" +
                 "^FDHello, World!^FS" +
                 "^XZ";

                
                try
                {
                    // Endereço IP e porta da impressora
                    string printerIP = "192.168.0.118";
                    string port = "9100"; // Porta padrão para impressoras de rede

                    // Combina o IP e a porta para o método openport
                    string printerConnection = $"IP_{printerIP}:{port}";

                    // Abrindo a porta para a impressora
                    int result = TSCLIB_DLL.openport(printerConnection);

                    if (result == 0)
                    {
                        Console.WriteLine("Conexão com a impressora estabelecida com sucesso.");

                        // Configurando a etiqueta: largura, altura, velocidade, densidade, tipo de sensor, posição vertical, e offset
                        TSCLIB_DLL.setup("100", "150", "4", "8", "0", "0", "0");

                        // Enviando comandos de impressão
                        TSCLIB_DLL.sendcommand("DENSITY 15");
                        TSCLIB_DLL.sendcommand("SPEED 5");
                        TSCLIB_DLL.sendcommand("SET TEAR ON");
                        TSCLIB_DLL.sendcommand("CODEPAGE 850");
                        TSCLIB_DLL.sendcommand("DIRECTION 1");
                        TSCLIB_DLL.sendcommand("SIZE 100 mm, 150 mm");
                        TSCLIB_DLL.sendcommand("GAP 3 mm, 0 mm");
                        TSCLIB_DLL.sendcommand("REFERENCE 0,0");

                        // Comandos específicos para imprimir texto e código de barras
                        TSCLIB_DLL.sendcommand("TEXT 100,100,\"3\",0,1,1,\"Hello, World!\"");
                        TSCLIB_DLL.sendcommand("BARCODE 100,200,\"128\",100,1,0,2,2,\"1234567890\"");

                        // Imprimindo a etiqueta
                        TSCLIB_DLL.printlabel("1", "1");

                        // Fechando a porta para a impressora
                        TSCLIB_DLL.closeport();

                        Console.WriteLine("Etiqueta impressa com sucesso!");
                    }
                    else
                    {
                        Console.WriteLine("Falha ao estabelecer conexão com a impressora.");
                    }
                }
                catch (Exception)
                {
                    // connection cannot be established
                }

                return Ok("Impressão concluída, verifique a impressora, por favor...");
            }
            return Ok("Produto não encontrado");
        }
    }
}
