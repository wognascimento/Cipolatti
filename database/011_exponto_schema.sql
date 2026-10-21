create table if not exists users (
    id text primary key,
    nome text not null,
    email text not null unique,
    password_hash text not null,
    email_verified_at timestamptz null,
    remember_token text null,
    must_change_password boolean not null default true,
    ativo boolean not null default true,
    master_user_id text null references users(id) on delete set null,
    integrante_vinculado_id text null,
    created_at timestamptz not null default now(),
    updated_at timestamptz null
);

create table if not exists sessions (
    id text primary key,
    user_id text not null references users(id) on delete cascade,
    device_id text not null,
    token text not null unique,
    created_at timestamptz not null default now(),
    expires_at timestamptz not null
);

create index if not exists ix_sessions_user_device on sessions(user_id, device_id);
create index if not exists ix_sessions_token on sessions(token);

create table if not exists user_devices (
    user_id text not null references users(id) on delete cascade,
    device_id text not null,
    status text not null default 'active',
    blocked_at timestamptz null,
    last_seen_at timestamptz null,
    created_at timestamptz not null default now(),
    updated_at timestamptz null,
    primary key (user_id, device_id)
);

create index if not exists ix_user_devices_user_status on user_devices(user_id, status);

create table if not exists funcoes_base (
    id text primary key,
    funcao text not null,
    funcao_normalizada text not null,
    created_at timestamptz not null default now(),
    updated_at timestamptz null,
    unique (funcao_normalizada)
);

create index if not exists ix_funcoes_base_normalizada on funcoes_base(funcao_normalizada);

create table if not exists funcoes (
    id text primary key,
    id_usuario text not null references users(id) on delete cascade,
    funcao_base_id text null references funcoes_base(id),
    funcao text not null,
    valor numeric(12,2) not null default 0,
    created_at timestamptz not null default now(),
    updated_at timestamptz null
);

create index if not exists ix_funcoes_usuario on funcoes(id_usuario);

create table if not exists integrantes (
    id text primary key,
    id_usuario text not null references users(id) on delete cascade,
    cpf text not null,
    nome text not null,
    funcao_id text not null references funcoes(id),
    biometria_facial text null,
    face_encoding double precision[] null,
    ativo boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz null
);

create index if not exists ix_integrantes_usuario on integrantes(id_usuario);
create index if not exists ix_integrantes_funcao on integrantes(funcao_id);
create index if not exists ix_users_master_user on users(master_user_id);

create table if not exists clientes (
    id text primary key,
    id_aprovado text not null,
    nome text not null,
    sigla text not null,
    latitude double precision not null,
    longitude double precision not null,
    raio double precision not null,
    created_at timestamptz not null default now(),
    updated_at timestamptz null
);

create unique index if not exists ux_clientes_id_aprovado on clientes(id_aprovado);

create table if not exists user_clientes (
    user_id text not null references users(id) on delete cascade,
    cliente_id text not null references clientes(id) on delete cascade,
    created_at timestamptz not null default now(),
    primary key (user_id, cliente_id)
);

create index if not exists ix_user_clientes_user on user_clientes(user_id);
create index if not exists ix_user_clientes_cliente on user_clientes(cliente_id);

create table if not exists cronograma_fases (
    id text primary key,
    cliente_id text not null references clientes(id) on delete cascade,
    fase text not null,
    data_inicio date not null,
    data_fim date not null,
    created_at timestamptz not null default now(),
    updated_at timestamptz null
);

create index if not exists ix_cronograma_fases_cliente_datas on cronograma_fases(cliente_id, data_inicio, data_fim);

create table if not exists cronograma_fase_funcoes (
    id text primary key,
    fase_id text not null references cronograma_fases(id) on delete cascade,
    funcao_id text null,
    funcao_base_id text null references funcoes_base(id),
    funcao_nome text null,
    limite_integrantes integer not null default 0,
    created_at timestamptz not null default now(),
    updated_at timestamptz null,
    unique (fase_id, funcao_base_id)
);

create index if not exists ix_cronograma_fase_funcoes_fase_funcao on cronograma_fase_funcoes(fase_id, funcao_id);
create unique index if not exists ux_cronograma_fase_funcoes_base on cronograma_fase_funcoes(fase_id, funcao_base_id) where funcao_base_id is not null;

create table if not exists integrante_emprestimos (
    id text primary key,
    integrante_id text not null references integrantes(id),
    equipe_origem_id text not null references users(id),
    equipe_destino_id text not null references users(id),
    data_inicio date not null,
    data_fim date not null,
    status text not null default 'ativo',
    devolvido_em timestamptz null,
    created_at timestamptz not null default now(),
    updated_at timestamptz null,
    check (data_fim >= data_inicio),
    check (equipe_origem_id <> equipe_destino_id)
);

create index if not exists ix_emprestimos_destino_periodo on integrante_emprestimos(equipe_destino_id, data_inicio, data_fim, status);
create index if not exists ix_emprestimos_origem on integrante_emprestimos(equipe_origem_id, status);

create table if not exists emprestimo_renovacoes (
    id text primary key,
    emprestimo_id text not null references integrante_emprestimos(id) on delete cascade,
    data_fim_atual date not null,
    nova_data_fim date not null,
    status text not null default 'pendente',
    approval_token text not null,
    solicitado_em timestamptz not null default now(),
    decidido_em timestamptz null,
    updated_at timestamptz null
);

create table if not exists notificacoes_usuario (
    id text primary key,
    usuario_id text not null references users(id) on delete cascade,
    tipo text not null,
    titulo text not null,
    mensagem text not null,
    referencia_id text null,
    dedup_key text null,
    lida_em timestamptz null,
    created_at timestamptz not null default now()
);

create unique index if not exists ux_notificacoes_usuario_dedup on notificacoes_usuario(usuario_id, dedup_key) where dedup_key is not null;

create table if not exists apontamentos (
    id text primary key,
    id_usuario text not null references users(id) on delete cascade,
    id_usuario_pagador text null references users(id),
    emprestimo_id text null references integrante_emprestimos(id),
    cliente_id text not null references clientes(id),
    integrante_id text not null references integrantes(id),
    tipo text not null check (tipo in ('entrada', 'saida')),
    latitude double precision not null,
    longitude double precision not null,
    timestamp timestamptz not null,
    dentro_do_raio boolean not null default false,
    biometria_verificada boolean not null default false,
    liveness_verificado boolean not null default false,
    offline_face_verified boolean not null default false,
    offline_device_biometric_verified boolean not null default false,
    synced_from_offline boolean not null default false,
    localizacao_status text not null default 'aprovado',
    solicitacao_ajuste_id text null,
    cronograma_fase_id text null references cronograma_fases(id),
    cronograma_fase_sigla text null,
    dia_extra boolean not null default false,
    funcao_extra boolean not null default false,
    cronograma_status text not null default 'aprovado',
    cronograma_excecao_id text null,
    cronograma_justificativa text null,
    manutencao_status text not null default 'aprovado',
    manutencao_excecao_id text null,
    created_at timestamptz not null default now()
);

alter table apontamentos add column if not exists id_usuario_pagador text null references users(id);
alter table apontamentos add column if not exists emprestimo_id text null references integrante_emprestimos(id);
update apontamentos set id_usuario_pagador = id_usuario where id_usuario_pagador is null;

create index if not exists ix_apontamentos_usuario_timestamp on apontamentos(id_usuario, timestamp desc);
create index if not exists ix_apontamentos_cliente_timestamp on apontamentos(cliente_id, timestamp desc);
create index if not exists ix_apontamentos_integrante_timestamp on apontamentos(integrante_id, timestamp desc);
create index if not exists ix_apontamentos_cronograma_status on apontamentos(id_usuario, cronograma_status);

create table if not exists cronograma_excecoes (
    id text primary key,
    id_usuario text not null references users(id) on delete cascade,
    apontamento_id text not null references apontamentos(id) on delete cascade,
    tipo text not null,
    status text not null default 'pendente',
    motivo text null,
    approval_token text null,
    decisao_motivo text null,
    decisao_usuario text null,
    decided_at timestamptz null,
    decided_by text null,
    notification_sent_at timestamptz null,
    created_at timestamptz not null default now(),
    updated_at timestamptz null
);

create index if not exists ix_cronograma_excecoes_usuario_status on cronograma_excecoes(id_usuario, status);
create index if not exists ix_cronograma_excecoes_token on cronograma_excecoes(approval_token);

create table if not exists solicitacoes_ajuste (
    id text primary key,
    id_usuario text not null references users(id) on delete cascade,
    cliente_id text not null references clientes(id),
    latitude_atual double precision not null,
    longitude_atual double precision not null,
    nova_latitude double precision not null,
    nova_longitude double precision not null,
    motivo text null,
    status text not null default 'pendente',
    approval_token text null,
    decided_at timestamptz null,
    decided_by text null,
    created_at timestamptz not null default now(),
    updated_at timestamptz null
);

create index if not exists ix_solicitacoes_usuario_status on solicitacoes_ajuste(id_usuario, status);

create or replace view vw_resumo_apontamentos_operacional as
with apontamentos_ordenados as (
    select
        a.*,
        lag(a.timestamp) over (
            partition by a.id_usuario, a.cliente_id
            order by a.timestamp, a.id
        ) as timestamp_anterior
    from apontamentos a
),
jornadas_marcadas as (
    select
        ao.*,
        case
            when ao.timestamp_anterior is null then 1
            when ao.timestamp - ao.timestamp_anterior > interval '18 hours' then 1
            else 0
        end as nova_jornada
    from apontamentos_ordenados ao
),
jornadas_numeradas as (
    select
        jm.*,
        sum(jm.nova_jornada) over (
            partition by jm.id_usuario, jm.cliente_id
            order by jm.timestamp, jm.id
            rows between unbounded preceding and current row
        ) as jornada_numero
    from jornadas_marcadas jm
),
base as (
    select
        jn.*,
        min(jn.timestamp) over (
            partition by jn.id_usuario, jn.cliente_id, jn.jornada_numero
        ) as inicio_jornada
    from jornadas_numeradas jn
),
pares as (
    select
        b.*,
        lead(b.tipo) over (
            partition by b.id_usuario, b.cliente_id, b.integrante_id, b.jornada_numero
            order by b.timestamp, b.id
        ) as proximo_tipo,
        lead(b.timestamp) over (
            partition by b.id_usuario, b.cliente_id, b.integrante_id, b.jornada_numero
            order by b.timestamp, b.id
        ) as proximo_timestamp
    from base b
),
agregado as (
    select
        p.id_usuario,
        p.id_usuario_pagador,
        p.cliente_id,
        p.integrante_id,
        p.jornada_numero,
        (min(p.inicio_jornada) at time zone 'America/Sao_Paulo')::date as data_apontamento,
        min(p.timestamp) filter (where p.tipo = 'entrada') as entrada,
        max(p.timestamp) filter (where p.tipo = 'saida') as saida,
        count(*) as quantidade_batidas,
        count(*) filter (where p.tipo = 'entrada') as quantidade_entradas,
        count(*) filter (where p.tipo = 'saida') as quantidade_saidas,
        coalesce(sum(
            case
                when p.tipo = 'entrada'
                 and p.proximo_tipo = 'saida'
                 and p.proximo_timestamp > p.timestamp
                    then extract(epoch from (p.proximo_timestamp - p.timestamp)) / 60
                else 0
            end
        ), 0)::integer as total_minutos,
        case
            when bool_or(coalesce(p.localizacao_status, 'aprovado') = 'recusado') then 'recusado'
            when bool_or(coalesce(p.localizacao_status, 'aprovado') = 'pendente_aprovacao') then 'pendente_aprovacao'
            else 'aprovado'
        end as localizacao_status,
        nullif((array_agg(p.cronograma_fase_sigla order by p.timestamp) filter (where p.cronograma_fase_sigla is not null))[1], '') as fase,
        bool_or(coalesce(p.dia_extra, false)) as dia_extra,
        bool_or(coalesce(p.funcao_extra, false)) as funcao_extra,
        case
            when bool_or(coalesce(p.cronograma_status, 'aprovado') = 'recusado') then 'recusado'
            when bool_or(coalesce(p.cronograma_status, 'aprovado') = 'pendente_aprovacao') then 'pendente_aprovacao'
            else 'aprovado'
        end as cronograma_status,
        case
            when bool_or(coalesce(p.manutencao_status, 'aprovado') = 'recusado') then 'recusado'
            when bool_or(coalesce(p.manutencao_status, 'aprovado') = 'pendente_aprovacao') then 'pendente_aprovacao'
            else 'aprovado'
        end as manutencao_status,
        bool_or(p.solicitacao_ajuste_id is not null) as possui_ajuste_localizacao,
        bool_or(p.cronograma_excecao_id is not null) as possui_excecao_cronograma,
        bool_or(p.manutencao_excecao_id is not null) as possui_excecao_manutencao
    from pares p
    group by p.id_usuario, p.id_usuario_pagador, p.cliente_id, p.integrante_id, p.jornada_numero
),
resumo as (
    select
        ag.*,
        (ag.quantidade_batidas % 2 <> 0 or ag.quantidade_entradas <> ag.quantidade_saidas) as batidas_impares,
        i.nome as integrante_nome,
        i.cpf as integrante_cpf,
        i.funcao_id,
        f.funcao,
        coalesce(f.valor, 0)::numeric(12,2) as valor_diaria,
        u.nome as usuario_nome,
        u.email as usuario_email,
        up.master_user_id,
        coalesce(up.master_user_id, up.id) as responsavel_id,
        coalesce(ump.nome, up.nome) as responsavel_nome,
        coalesce(ump.email, up.email) as responsavel_email,
        c.id_aprovado as cliente_id_aprovado,
        c.sigla as cliente_sigla,
        c.nome as cliente_nome
    from agregado ag
    join integrantes i on i.id = ag.integrante_id
    join funcoes f on f.id = i.funcao_id
    join users u on u.id = ag.id_usuario
    join users up on up.id = coalesce(ag.id_usuario_pagador, ag.id_usuario)
    left join users ump on ump.id = up.master_user_id
    join clientes c on c.id = ag.cliente_id
),
calculado as (
    select
        r.*,
        (
            not r.batidas_impares
            and r.localizacao_status = 'aprovado'
            and r.cronograma_status = 'aprovado'
            and r.manutencao_status = 'aprovado'
        ) as aprovado,
        case
            when r.batidas_impares then 'batida_impar'
            when r.localizacao_status = 'recusado'
              or r.cronograma_status = 'recusado'
              or r.manutencao_status = 'recusado' then 'recusado'
            when r.localizacao_status = 'pendente_aprovacao'
              or r.cronograma_status = 'pendente_aprovacao'
              or r.manutencao_status = 'pendente_aprovacao' then 'pendente_aprovacao'
            else 'aprovado'
        end as status_resumo
    from resumo r
)
select
    calculado.id_usuario as usuario_id,
    calculado.usuario_nome,
    calculado.usuario_email,
    calculado.master_user_id,
    calculado.responsavel_id,
    calculado.responsavel_nome,
    calculado.responsavel_email,
    calculado.cliente_id,
    calculado.cliente_id_aprovado,
    calculado.cliente_sigla,
    calculado.cliente_nome,
    calculado.data_apontamento,
    to_char(calculado.data_apontamento, 'DD/MM/YYYY') as data_apontamento_br,
    calculado.integrante_id,
    calculado.integrante_nome,
    calculado.integrante_cpf,
    calculado.funcao_id,
    calculado.funcao,
    calculado.entrada,
    calculado.saida,
    to_char(calculado.entrada at time zone 'America/Sao_Paulo', 'HH24:MI') as entrada_hora,
    to_char(calculado.saida at time zone 'America/Sao_Paulo', 'HH24:MI') as saida_hora,
    calculado.total_minutos,
    round((calculado.total_minutos::numeric / 60), 2) as total_horas,
    calculado.quantidade_batidas,
    calculado.batidas_impares,
    case when calculado.batidas_impares then 0 else 1 end as total_pessoas_base,
    calculado.valor_diaria,
    case when calculado.aprovado then calculado.valor_diaria else 0::numeric(12,2) end as valor_aprovado,
    case when calculado.aprovado then 0::numeric(12,2) else calculado.valor_diaria end as valor_nao_aprovado,
    calculado.localizacao_status,
    calculado.fase,
    calculado.dia_extra,
    calculado.funcao_extra,
    calculado.cronograma_status,
    calculado.manutencao_status,
    calculado.possui_ajuste_localizacao,
    calculado.possui_excecao_cronograma,
    calculado.possui_excecao_manutencao,
    calculado.status_resumo,
    sum(case when calculado.batidas_impares then 0 else 1 end) over (
        partition by calculado.responsavel_id, calculado.cliente_id, calculado.data_apontamento
    ) as total_pessoas_dia,
    sum(case when calculado.aprovado then calculado.valor_diaria else 0::numeric(12,2) end) over (
        partition by calculado.responsavel_id, calculado.cliente_id, calculado.data_apontamento
    ) as valor_aprovado_dia,
    sum(case when calculado.aprovado then 0::numeric(12,2) else calculado.valor_diaria end) over (
        partition by calculado.responsavel_id, calculado.cliente_id, calculado.data_apontamento
    ) as valor_nao_aprovado_dia
from calculado;

create or replace view vw_resumo_apontamentos_dia_responsavel as
select
    responsavel_id,
    responsavel_nome,
    responsavel_email,
    cliente_id,
    cliente_id_aprovado,
    cliente_sigla,
    cliente_nome,
    data_apontamento,
    data_apontamento_br,
    max(total_pessoas_dia) as total_pessoas,
    round(sum(total_minutos)::numeric / 60, 2) as total_horas,
    max(valor_aprovado_dia) as valor_aprovado,
    max(valor_nao_aprovado_dia) as valor_nao_aprovado,
    max(valor_aprovado_dia) + max(valor_nao_aprovado_dia) as total_valor,
    bool_or(batidas_impares) as possui_ajustes,
    bool_or(dia_extra) as possui_dia_extra,
    bool_or(funcao_extra) as possui_funcao_extra,
    bool_or(possui_ajuste_localizacao) as possui_ajuste_localizacao,
    bool_or(possui_excecao_cronograma) as possui_excecao_cronograma,
    bool_or(possui_excecao_manutencao) as possui_excecao_manutencao,
    case
        when bool_or(status_resumo = 'recusado') then 'recusado'
        when bool_or(status_resumo in ('pendente_aprovacao', 'batida_impar')) then 'pendente_aprovacao'
        else 'aprovado'
    end as status_resumo,
    count(*) as total_integrantes_com_apontamento,
    count(*) filter (where status_resumo = 'aprovado') as total_integrantes_aprovados,
    count(*) filter (where status_resumo <> 'aprovado') as total_integrantes_nao_aprovados
from vw_resumo_apontamentos_operacional
group by
    responsavel_id,
    responsavel_nome,
    responsavel_email,
    cliente_id,
    cliente_id_aprovado,
    cliente_sigla,
    cliente_nome,
    data_apontamento,
    data_apontamento_br;

create or replace view vw_resumo_apontamentos_relatorio as
select
    responsavel_id,
    responsavel_nome,
    cliente_sigla as cliente,
    data_apontamento as data,
    data_apontamento_br as data_br,
    integrante_nome as integrante,
    funcao,
    entrada_hora as entrada,
    saida_hora as saida,
    case
        when batidas_impares then 0::numeric
        else total_horas
    end as total_de_horas,
    valor_aprovado as valor,
    localizacao_status as status_localizacao,
    coalesce(fase, '-') as fase,
    case when dia_extra then 'Sim' else 'Nao' end as dia_extra,
    case when funcao_extra then 'Sim' else 'Nao' end as funcao_extra,
    cronograma_status as status_cronograma,
    manutencao_status as status_manutencao
from vw_resumo_apontamentos_operacional;

create or replace view vw_cronograma_dias_sem_apontamento as
with responsaveis_clientes as (
    select distinct
        coalesce(u.master_user_id, u.id) as responsavel_id,
        coalesce(um.nome, u.nome) as responsavel_nome,
        coalesce(um.email, u.email) as responsavel_email,
        uc.cliente_id
    from user_clientes uc
    join users u on u.id = uc.user_id
    left join users um on um.id = u.master_user_id
    where u.ativo = true

    union

    select distinct
        coalesce(u.master_user_id, u.id) as responsavel_id,
        coalesce(um.nome, u.nome) as responsavel_nome,
        coalesce(um.email, u.email) as responsavel_email,
        a.cliente_id
    from apontamentos a
    join users u on u.id = a.id_usuario
    left join users um on um.id = u.master_user_id
    where u.ativo = true
),
cronograma_dias as (
    select
        cf.id as fase_id,
        cf.cliente_id,
        cf.fase,
        gs.data::date as data_cronograma
    from cronograma_fases cf
    cross join lateral generate_series(cf.data_inicio, cf.data_fim, interval '1 day') as gs(data)
    where gs.data::date <= (now() at time zone 'America/Sao_Paulo')::date
),
funcoes_previstas as (
    select
        cff.fase_id,
        string_agg(
            concat(coalesce(cff.funcao_nome, fb.funcao, 'Funcao'), ' (', cff.limite_integrantes, ')'),
            ', '
            order by coalesce(cff.funcao_nome, fb.funcao, 'Funcao')
        ) as funcoes_previstas,
        count(*) as total_funcoes_previstas
    from cronograma_fase_funcoes cff
    left join funcoes_base fb on fb.id = cff.funcao_base_id
    group by cff.fase_id
)
select
    rc.responsavel_id,
    rc.responsavel_nome,
    rc.responsavel_email,
    c.id as cliente_id,
    c.id_aprovado as cliente_id_aprovado,
    c.sigla as cliente_sigla,
    c.nome as cliente_nome,
    cd.fase_id,
    cd.fase,
    cd.data_cronograma,
    to_char(cd.data_cronograma, 'DD/MM/YYYY') as data_cronograma_br,
    coalesce(fp.funcoes_previstas, '') as funcoes_previstas,
    coalesce(fp.total_funcoes_previstas, 0) as total_funcoes_previstas,
    'sem_apontamento'::text as status_envio
from cronograma_dias cd
join clientes c on c.id = cd.cliente_id
join responsaveis_clientes rc on rc.cliente_id = cd.cliente_id
left join funcoes_previstas fp on fp.fase_id = cd.fase_id
left join vw_resumo_apontamentos_dia_responsavel rd
    on rd.responsavel_id = rc.responsavel_id
   and rd.cliente_id = cd.cliente_id
   and rd.data_apontamento = cd.data_cronograma
where rd.responsavel_id is null;
