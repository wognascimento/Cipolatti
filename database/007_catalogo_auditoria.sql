-- Completa a auditoria das tabelas administrativas do Catalogo Virtual.
-- Alteracao aditiva e nao destrutiva.
BEGIN;

ALTER TABLE comercial.tema_catalogo
    ADD COLUMN IF NOT EXISTS inserido_por character varying(120),
    ADD COLUMN IF NOT EXISTS alterado_por character varying(120);

ALTER TABLE comercial.youtube_integracao
    ADD COLUMN IF NOT EXISTS autorizado_por character varying(120),
    ADD COLUMN IF NOT EXISTS alterado_por character varying(120);

ALTER TABLE comercial.youtube_upload
    ADD COLUMN IF NOT EXISTS inserido_por character varying(120),
    ADD COLUMN IF NOT EXISTS alterado_por character varying(120);

COMMENT ON COLUMN comercial.tema_catalogo.inserido_por IS 'Login do AD que incluiu o registro no catalogo.';
COMMENT ON COLUMN comercial.tema_catalogo.alterado_por IS 'Login do AD que realizou a ultima alteracao.';

COMMIT;
