-- Catalogo Virtual Cipolatti: metadados editoriais especificos do novo canal.
-- Nao altera comercial.temas nem reaproveita campos legados.
BEGIN;

CREATE TABLE IF NOT EXISTS comercial.tema_catalogo (
    idtema integer PRIMARY KEY,
    slug character varying(180) NOT NULL,
    nome_publico character varying(254),
    imagem_capa character varying(500),
    descricao_catalogo text,
    publicado boolean NOT NULL DEFAULT false,
    destaque boolean NOT NULL DEFAULT false,
    ordem integer NOT NULL DEFAULT 0,
    data_publicacao timestamp with time zone,
    data_cadastro timestamp with time zone NOT NULL DEFAULT now(),
    data_altera timestamp with time zone,
    CONSTRAINT uq_tema_catalogo_slug UNIQUE (slug),
    CONSTRAINT ck_tema_catalogo_slug_formato
        CHECK (slug = lower(slug) AND slug ~ '^[a-z0-9]+(?:-[a-z0-9]+)*$'),
    CONSTRAINT ck_tema_catalogo_ordem CHECK (ordem >= 0),
    CONSTRAINT fk_tema_catalogo_tema FOREIGN KEY (idtema)
        REFERENCES comercial.temas(idtema)
);

CREATE INDEX IF NOT EXISTS ix_tema_catalogo_publicacao
    ON comercial.tema_catalogo (publicado, destaque, ordem, idtema);

COMMENT ON TABLE comercial.tema_catalogo IS
    'Metadados editoriais exclusivos do Catalogo Virtual Cipolatti.';
COMMENT ON COLUMN comercial.tema_catalogo.nome_publico IS
    'Nome editorial opcional; quando nulo/vazio, a aplicacao usa comercial.temas.temas.';
COMMENT ON COLUMN comercial.tema_catalogo.imagem_capa IS
    'Path ou URL de metadado; o binario nao e armazenado no PostgreSQL.';

COMMIT;

