BEGIN;

CREATE TABLE IF NOT EXISTS expedicao.app_dispositivos_fotos (
    id bigserial PRIMARY KEY,
    nome varchar(100) NOT NULL,
    token_hash char(64) UNIQUE,
    codigo_ativacao_hash char(64) UNIQUE,
    codigo_ativacao_expira_em timestamptz,
    ativo boolean NOT NULL DEFAULT false,
    criado_em timestamptz NOT NULL DEFAULT now(),
    ativado_em timestamptz,
    ultimo_acesso_em timestamptz,
    revogado_em timestamptz
);

CREATE TABLE IF NOT EXISTS expedicao.romaneio_foto_finalizacoes (
    id bigserial PRIMARY KEY,
    codigo_romaneio integer NOT NULL REFERENCES expedicao.t_romaneio(cod_romaneiro),
    dispositivo_id bigint NOT NULL REFERENCES expedicao.app_dispositivos_fotos(id),
    chave_idempotencia uuid NOT NULL,
    pasta text NOT NULL,
    arquivos_json jsonb NOT NULL,
    observacao varchar(1000),
    email_enviado boolean NOT NULL DEFAULT false,
    email_enviado_em timestamptz,
    concluido_em timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_romaneio_foto_dispositivo_operacao UNIQUE (dispositivo_id, chave_idempotencia)
);

CREATE INDEX IF NOT EXISTS ix_romaneio_foto_finalizacoes_romaneio
    ON expedicao.romaneio_foto_finalizacoes(codigo_romaneio, concluido_em DESC);

COMMIT;
