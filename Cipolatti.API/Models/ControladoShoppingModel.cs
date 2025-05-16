using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace Cipolatti.API.Models;

[Table("tbl_controlado_shopping", Schema = "producao")]
public class ControladoShoppingModel
{
    [Key, Column("num_requisicao", Order = 0)]
    public int NumRequisicao { get; set; }

    [Key, Column("barcode", Order = 1)]
    [StringLength(15)]
    public string Barcode { get; set; } = string.Empty;

    [Column("inserido_por")]
    [StringLength(50)]
    public string? InseridoPor { get; set; }

    [Column("inserido_em")]
    public DateTime? InseridoEm { get; set; }

    [Column("retorno")]
    [StringLength(30)]
    public string? Retorno { get; set; }
}
