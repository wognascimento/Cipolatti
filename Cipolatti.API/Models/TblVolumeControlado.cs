﻿// <auto-generated> This file has been auto generated by EF Core Power Tools. </auto-generated>
#nullable disable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Models;

[Table("tbl_volume_controlado", Schema = "producao")]
public partial class TblVolumeControlado
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("sigla")]
    [StringLength(50)]
    public string Sigla { get; set; }

    [Column("volume")]
    public int Volume { get; set; }

    [Column("conferido")]
    public DateOnly Conferido { get; set; }

    [Column("recebido")]
    public DateOnly? Recebido { get; set; }
}