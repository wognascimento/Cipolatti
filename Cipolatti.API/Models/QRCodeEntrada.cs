using System.ComponentModel.DataAnnotations;
namespace Cipolatti.API.Models;

public class QRCodeEntrada
{
    public int Id { get; set; }
    [Required, StringLength(15)] public string Barcode { get; set; } = string.Empty;
    [Required, StringLength(50)] public string InseridoPor { get; set; } = string.Empty;
    public DateTime InseridoEm { get; set; }
}
