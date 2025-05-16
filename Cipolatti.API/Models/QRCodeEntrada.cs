namespace Cipolatti.API.Models;

public class QRCodeEntrada
{
    public int Id { get; set; }
    public string Barcode { get; set; }
    public string InseridoPor { get; set; }
    public DateTime InseridoEm { get; set; }
}

