using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cipolatti.API.Models;

[Table("t_detalhes_req", Schema = "producao")]
public class DetalheRequisicaoModel
{
    [Key]
    public long? cod_det_req { set; get; }
    public long? num_requisicao { set; get; }
    public double? quantidade { set; get; }
    public DateTime? data { set; get; }
    public string? alterado_por { set; get; }
    public string? ok { set; get; }
    public DateTime? data_ok { set; get; }
    public string? ok_expedido { set; get; }
    public string? observacao { set; get; }
    public string? voltagem { set; get; }
    public string? local_shop { set; get; }
    public long? complemento_chk { set; get; }
    public long? codcompladicional { set; get; }
    public long? volume { set; get; }
    public long? dividir_qtd_volume { set; get; }
    public bool? agupar { set; get; }
}

