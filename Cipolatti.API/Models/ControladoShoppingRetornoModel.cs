using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cipolatti.API.Models;

[Table("tbl_controlado_shopping_retorno", Schema = "producao")]
public class ControladoShoppingRetornoModel
{
    [Key, Column("barcode", Order = 0)]
    public string Barcode { get; set; } = null!;
    [Column("inserido_por")]
    public string InseridoPor { get; set; } = null!;
    [Key, Column("inserido_em", Order = 1)]
    public DateTime InseridoEm { get; set; }
}

