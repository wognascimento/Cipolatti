-- Adiciona o marcador editorial de tema inedito ao Catalogo Virtual.
BEGIN;

ALTER TABLE comercial.tema_catalogo
    ADD COLUMN IF NOT EXISTS inedito boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN comercial.tema_catalogo.inedito IS
    'Indica que o tema deve ser apresentado como inedito no Catalogo Virtual.';

COMMIT;
