﻿// <auto-generated> This file has been auto generated by EF Core Power Tools. </auto-generated>
#nullable disable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Models;

[Keyless]
public partial class Qry3descricoes
{
    [Column("planilha")]
    [StringLength(50)]
    public string Planilha { get; set; }

    [Column("descricao")]
    [StringLength(254)]
    public string Descricao { get; set; }

    [Column("descricao_adicional")]
    [StringLength(254)]
    public string DescricaoAdicional { get; set; }

    [Column("complementoadicional")]
    [StringLength(150)]
    public string Complementoadicional { get; set; }

    [Column("codcompladicional")]
    public int? Codcompladicional { get; set; }

    [Column("unidade")]
    [StringLength(10)]
    public string Unidade { get; set; }

    [Column("inativo")]
    [StringLength(5)]
    public string Inativo { get; set; }

    [Column("prodcontrolado")]
    [StringLength(5)]
    public string Prodcontrolado { get; set; }

    [Column("vida_util")]
    public short? VidaUtil { get; set; }

    [Column("diverso")]
    [StringLength(5)]
    public string Diverso { get; set; }

    [Column("custo")]
    public float? Custo { get; set; }

    [Column("coduniadicional")]
    public int? Coduniadicional { get; set; }

    [Column("descricaofiscal")]
    [StringLength(60)]
    public string Descricaofiscal { get; set; }

    [Column("descricaoespanhol")]
    [StringLength(254)]
    public string Descricaoespanhol { get; set; }

    [Column("familia")]
    [StringLength(50)]
    public string Familia { get; set; }

    [Column("descricao_completa")]
    public string DescricaoCompleta { get; set; }

    [Column("codigo")]
    public int? Codigo { get; set; }

    [Column("saldo_estoque")]
    [Precision(12, 2)]
    public decimal? SaldoEstoque { get; set; }

    [Column("classe_compra")]
    public string ClasseCompra { get; set; }

    [Column("estoque_min")]
    public float? EstoqueMin { get; set; }

    [Column("m3")]
    [Precision(12, 2)]
    public decimal? M3 { get; set; }

    [Column("pl")]
    [Precision(12, 2)]
    public decimal? Pl { get; set; }

    [Column("pb")]
    [Precision(12, 2)]
    public decimal? Pb { get; set; }

    [Column("estoque_inicial")]
    public float? EstoqueInicial { get; set; }
}