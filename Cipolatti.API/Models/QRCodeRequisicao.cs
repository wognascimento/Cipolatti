using System.ComponentModel.DataAnnotations;
namespace Cipolatti.API.Models;

public class QRCodeRequisicao
{
    public int? Id { get; set; }
    public int NumeroRequisicao { get; set; }
    [Required, StringLength(15)] public required string Barcode { get; set; }
    [Required, StringLength(50)] public string InseridoPor { get; set; } = string.Empty;
    public DateTime InseridoEm { get; set; }
}
