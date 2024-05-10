﻿// <auto-generated> This file has been auto generated by EF Core Power Tools. </auto-generated>
#nullable disable
using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace Cipolatti.API.Models;

public partial class CipolattiContext : DbContext
{
    public CipolattiContext(DbContextOptions<CipolattiContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Funcionarios> Funcionarios { get; set; }

    public virtual DbSet<Qry3descricoes> Qry3descricoes { get; set; }

    public virtual DbSet<QryAprovados> QryAprovados { get; set; }

    public virtual DbSet<QryEnderecamentoGalpao> QryEnderecamentoGalpao { get; set; }

    public virtual DbSet<QryLookup> QryLookup { get; set; }

    public virtual DbSet<TConfCargaGeral> TConfCargaGeral { get; set; }

    public virtual DbSet<TSaidaAlmox> TSaidaAlmox { get; set; }

    public virtual DbSet<TblAtendentesAlmox> TblAtendentesAlmox { get; set; }

    public virtual DbSet<TblMovimentacaoVolumeShopping> TblMovimentacaoVolumeShopping { get; set; }

    public virtual DbSet<TblVolumeControlado> TblVolumeControlado { get; set; }


    static CipolattiContext() => AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasPostgresExtension("pg_catalog", "pg_cron")
            .HasPostgresExtension("postgres_fdw")
            .HasPostgresExtension("uuid-ossp");

        modelBuilder.Entity<Funcionarios>(entity =>
        {
            entity.HasKey(e => e.Codfun).HasName("funcionarios_pkey");

            entity.Property(e => e.Ativo).IsFixedLength();
            entity.Property(e => e.Cpd)
                .HasDefaultValueSql("0")
                .IsFixedLength();
            entity.Property(e => e.ExibirFuro).HasDefaultValueSql("'-1'::integer");
            entity.Property(e => e.Impresso)
                .HasDefaultValueSql("0")
                .IsFixedLength();
            entity.Property(e => e.Ncalcado).HasDefaultValueSql("0");
            entity.Property(e => e.OcultarDados).HasDefaultValueSql("0");
            entity.Property(e => e.PrazoContrato).HasDefaultValue(0);
            entity.Property(e => e.PrazoRenovaContrato).HasDefaultValue(0);
        });

        modelBuilder.Entity<Qry3descricoes>(entity =>
        {
            entity.ToView("qry3descricoes", "producao");

            entity.Property(e => e.Inativo).IsFixedLength();
        });

        modelBuilder.Entity<QryAprovados>(entity =>
        {
            entity.ToView("qry_aprovados", "producao");
        });

        modelBuilder.Entity<QryEnderecamentoGalpao>(entity =>
        {
            entity.ToView("qry_enderecamento_galpao", "expedicao");
        });

        modelBuilder.Entity<QryLookup>(entity =>
        {
            entity.ToView("qry_lookup", "expedicao");
        });

        modelBuilder.Entity<TConfCargaGeral>(entity =>
        {
            entity.HasKey(e => e.Barcode).HasName("t_conf_carga_geral_pkey");

            entity.Property(e => e.Entradasistema).HasDefaultValueSql("(now())::timestamp(0) with time zone");
        });

        modelBuilder.Entity<TSaidaAlmox>(entity =>
        {
            entity.HasKey(e => e.CodSaidaAlmox).HasName("t_saida_almox_pkey");

            entity.Property(e => e.DataInclusao).HasDefaultValueSql("(now())::timestamp(0) with time zone");
            entity.Property(e => e.Expurgo)
                .HasDefaultValueSql("0")
                .IsFixedLength();
            entity.Property(e => e.Hora).HasDefaultValueSql("(now())::timestamp(0) with time zone");
            entity.Property(e => e.IncluidoPor).HasDefaultValueSql("CURRENT_USER");
        });

        modelBuilder.Entity<TblAtendentesAlmox>(entity =>
        {
            entity.HasKey(e => e.NomeFuncionario).HasName("tbl_atendentes_almox_pkey");

            entity.ToTable("tbl_atendentes_almox", "almoxarifado_jac", tb => tb.HasComment("Funcionarios de atendimento do Almoxarifado."));
        });

        modelBuilder.Entity<TblMovimentacaoVolumeShopping>(entity =>
        {
            entity.HasKey(e => e.IdLinhaInserida).HasName("tbl_movimentacao_volume_shopping_pkey");

            entity.Property(e => e.InseridoEm).HasDefaultValueSql("(now())::timestamp(0) with time zone");
        });

        modelBuilder.Entity<TblVolumeControlado>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("tbl_volume_controlado_pkey");

            entity.Property(e => e.Recebido).HasDefaultValueSql("CURRENT_DATE");
        });
        modelBuilder.HasSequence("carregamento_itens_shopp_seq", "expedicao");
        modelBuilder.HasSequence("conceito_idconceito_seq", "comercial");
        modelBuilder.HasSequence("fluxo_id", "financeiro");
        modelBuilder.HasSequence("fluxo_linha_fluxo_seq", "financeiro");
        modelBuilder.HasSequence("fornecedores_idfornecedor_seq", "compras");
        modelBuilder.HasSequence("funcionarios_codfun_seq", "ht");
        modelBuilder.HasSequence("hibernate_sequence");
        modelBuilder.HasSequence("id_temporario", "equipe_externa").HasMin(0L);
        modelBuilder.HasSequence("idviewchklist", "equipe_externa").HasMin(0L);
        modelBuilder.HasSequence("idviewchklist", "producao").HasMin(0L);
        modelBuilder.HasSequence("itens_faltantes_id_seq", "expedicao");
        modelBuilder.HasSequence("jobid_seq", "cron");
        modelBuilder.HasSequence("pedidos_idpedido_seq", "compras").HasMin(0L);
        modelBuilder.HasSequence("pedidosdet_iddetpedido_seq", "compras").HasMin(0L);
        modelBuilder.HasSequence("produtos_codigo_seq", "producao");
        modelBuilder.HasSequence("proposta_base_preco_zefe_codbase_preco_seq", "comercial");
        modelBuilder.HasSequence("proposta_descricaocomercial_coddesccoml_seq", "comercial");
        modelBuilder.HasSequence("proposta_dimensaodescricaocomercial_coddimensao_seq", "comercial");
        modelBuilder.HasSequence("runid_seq", "cron");
        modelBuilder.HasSequence("seq_gen_sequence")
            .StartsAt(50L)
            .IncrementsBy(50);
        modelBuilder.HasSequence("solicitacao_material_cod_solicitacao_seq", "compras").HasMin(0L);
        modelBuilder.HasSequence("solicitacao_material_itens_cod_item_seq", "compras").HasMin(0L);
        modelBuilder.HasSequence("solicitacao_solicitantes_cod_solicitante_seq", "compras");
        modelBuilder.HasSequence("t_caixas_cod_caixa_seq", "expedicao");
        modelBuilder.HasSequence("t_desp_funcionario_cod_func_seq", "operacional");
        modelBuilder.HasSequence("t_detalhes_req_cod_det_req_seq", "producao").HasMin(0L);
        modelBuilder.HasSequence("tabela_desc_adicional_coduniadicional_seq", "producao");
        modelBuilder.HasSequence("tbl_aderecamento_id_aderecamento_seq", "projetos");
        modelBuilder.HasSequence("tbl_adereco_id_adereco_seq", "projetos").StartsAt(6243L);
        modelBuilder.HasSequence("tbl_agendavt_id_seq", "projetos");
        modelBuilder.HasSequence("tbl_banco_hora_cod_linha_banco_horas_seq", "rh");
        modelBuilder.HasSequence("tbl_condicoes_pagto_id_cond_pagamento_seq", "compras");
        modelBuilder.HasSequence("tbl_produto_os_num_os_produto_seq", "producao").HasMin(0L);
        modelBuilder.HasSequence("tbl_produtos_servico_num_os_servico_seq", "producao").HasMin(0L);
        modelBuilder.HasSequence("tbl_retencao_clientes_id_cliente_retencao_seq", "financeiro").StartsAt(304L);
        modelBuilder.HasSequence("tbl_retencao_faturamento_id_retencao_faturamento_seq", "financeiro").StartsAt(1763L);
        modelBuilder.HasSequence("tbl_retencoes_pagamentos_id_retencao_seq", "financeiro").StartsAt(395L);
        modelBuilder.HasSequence("tbl_setor_codigo_setor_seq", "producao");
        modelBuilder.HasSequence("tbl_turno_catraca_cod_linha_turno_seq", "ht");
        modelBuilder.HasSequence("tblcomplementoadicional_codcompladicional_seq", "producao");
        modelBuilder.HasSequence("tblCorrigirCompras_Controle_seq", "compras");
        modelBuilder.HasSequence("tblcotacao_codigo_cotacao_seq", "compras");
        modelBuilder.HasSequence("tblcotacao_detalhes_codigo_detalhes_cotacao_seq", "compras");
        modelBuilder.HasSequence("tblcotacao_fornecedor_codigo_cotacao_fornecedor_seq", "compras");
        modelBuilder.HasSequence("tbldetalhesolicit_coddetalhe_seq", "compras");
        modelBuilder.HasSequence("tblDetPedido_Cod_Det_Pedido_seq", "compras");
        modelBuilder.HasSequence("tblEmpresa_codigo_seq", "compras");
        modelBuilder.HasSequence("tblequipesext_id_seq", "equipe_externa");
        modelBuilder.HasSequence("tblFamiliaProd_CodigoFamilia_seq", "compras");
        modelBuilder.HasSequence("tblFornecedor_Controle_seq", "compras");
        modelBuilder.HasSequence("tblfornecedor_forn_codigo_seq", "compras").HasMin(0L);
        modelBuilder.HasSequence("tblPedidos_Cod_Pedido_seq", "compras");
        modelBuilder.HasSequence<int>("tblprodutofornecedor_id_seq", "compras");
        modelBuilder.HasSequence("tblProdutos_Cod_Produto_seq", "compras");
        modelBuilder.HasSequence("tblRecebeAlmox_Cod_Receb_seq", "compras");
        modelBuilder.HasSequence<int>("tblsetores_codigo_setor_seq", "rh");
        modelBuilder.HasSequence<int>("tblsetorescidades_codigosetorcidade_seq", "rh");
        modelBuilder.HasSequence<int>("tblsetoreslocais_codigosetorlocal_seq", "rh");
        modelBuilder.HasSequence("tblSolicitacao_CodSolicitacao_seq", "compras");
        modelBuilder.HasSequence("tblsolicitacoes_codsolicit_seq", "compras");
        modelBuilder.HasSequence<int>("tblsub_setores_codigo_sub_setor_seq", "rh");
        modelBuilder.HasSequence("temas_idtema_seq", "comercial");
        modelBuilder.HasSequence("temporario", "comercial").HasMin(0L);

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}