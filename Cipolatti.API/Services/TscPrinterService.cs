using System.Runtime.InteropServices;
using Cipolatti.API.Models;
namespace Cipolatti.API.Services;
public sealed partial class TscPrinterService(IConfiguration configuration, ILogger<TscPrinterService> logger) : IPrinterService
{
    private static readonly SemaphoreSlim PrinterLock = new(1, 1);
    public async Task PrintProductLabelAsync(Qry3descricoes product, CancellationToken cancellationToken)
    {
        await PrinterLock.WaitAsync(cancellationToken);
        var opened = false;
        try
        {
            var connection = configuration["Printer:Connection"] ?? throw new InvalidOperationException("Printer:Connection não configurado.");
            if (Native.openport(connection) != 0) throw new IOException("Não foi possível abrir a impressora.");
            opened = true;
            Native.setup("100", "150", "4", "8", "0", "0", "0");
            Native.sendcommand("CODEPAGE 850");
            Native.sendcommand($"TEXT 100,100,\"3\",0,1,1,\"{Escape(product.DescricaoCompleta ?? product.Descricao)}\"");
            Native.sendcommand($"BARCODE 100,200,\"128\",100,1,0,2,2,\"{product.Codcompladicional}\"");
            if (Native.printlabel("1", "1") != 0) throw new IOException("A impressora recusou a etiqueta.");
            logger.LogInformation("Etiqueta do produto {ProductCode} impressa.", product.Codcompladicional);
        }
        finally { if (opened) Native.closeport(); PrinterLock.Release(); }
    }
    private static string Escape(string? value) => (value ?? string.Empty).Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");
    private static partial class Native
    {
        [LibraryImport("TSCLIB.dll", EntryPoint = "openport", StringMarshalling = StringMarshalling.Utf8)] internal static partial int openport(string name);
        [LibraryImport("TSCLIB.dll", EntryPoint = "setup", StringMarshalling = StringMarshalling.Utf8)] internal static partial int setup(string width, string height, string speed, string density, string sensor, string vertical, string offset);
        [LibraryImport("TSCLIB.dll", EntryPoint = "sendcommand", StringMarshalling = StringMarshalling.Utf8)] internal static partial int sendcommand(string command);
        [LibraryImport("TSCLIB.dll", EntryPoint = "printlabel", StringMarshalling = StringMarshalling.Utf8)] internal static partial int printlabel(string set, string copy);
        [LibraryImport("TSCLIB.dll", EntryPoint = "closeport")] internal static partial int closeport();
    }
}
