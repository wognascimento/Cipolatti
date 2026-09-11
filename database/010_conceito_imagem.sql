BEGIN;
ALTER TABLE comercial.conceito ADD COLUMN IF NOT EXISTS imagem_url varchar(500);
COMMIT;
