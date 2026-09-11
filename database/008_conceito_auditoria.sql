-- Habilita auditoria das inclusoes e alteracoes de conceitos pelo Catalogo Virtual.
BEGIN;

ALTER TABLE comercial.conceito
    ADD COLUMN IF NOT EXISTS inserido_por character varying(120),
    ADD COLUMN IF NOT EXISTS data_cadastro timestamp with time zone,
    ADD COLUMN IF NOT EXISTS alterado_por character varying(120),
    ADD COLUMN IF NOT EXISTS data_altera timestamp with time zone;

COMMENT ON COLUMN comercial.conceito.inserido_por IS 'Login do AD que cadastrou o conceito pelo Catalogo Virtual.';
COMMENT ON COLUMN comercial.conceito.alterado_por IS 'Login do AD que realizou a ultima alteracao pelo Catalogo Virtual.';

COMMIT;
