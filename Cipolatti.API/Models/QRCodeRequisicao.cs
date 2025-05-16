namespace Cipolatti.API.Models;

public class QRCodeRequisicao
{
    public int? Id { get; set; }
    public int NumeroRequisicao { get; set; }
    public required string Barcode { get; set; }
    public string InseridoPor { get; set; }
    public DateTime InseridoEm { get; set; }
}

