# Scripts de banco

Os scripts sao aditivos e devem ser executados em ordem, com parada obrigatoria no primeiro erro:

1. `001_saida_almox_sequence.sql`
2. `002_tema_catalogo.sql`
3. `003_tema_pdf.sql`
4. `004_tema_pdf_pagina.sql`
5. `005_tema_video.sql`

Para a Fase 2 do Catalogo Virtual, execute apenas `002` a `005` caso o script `001` ja tenha sido tratado pelo sistema existente.

Exemplo com `psql`:

```bash
psql -v ON_ERROR_STOP=1 "$CONNECTION_STRING" \
  -f database/002_tema_catalogo.sql \
  -f database/003_tema_pdf.sql \
  -f database/004_tema_pdf_pagina.sql \
  -f database/005_tema_video.sql
```

Cada arquivo possui sua propria transacao. Nenhum script contem `DROP`, `TRUNCATE`, exclusao ou alteracao de tabela legada.

## Permissoes

O executor precisa poder criar objetos no schema `comercial` e criar FKs que referenciam `comercial.temas(idtema)`. Na validacao de 8/9/2026, o usuario tecnico `codex` tinha `USAGE`, `SELECT` e `REFERENCES` sobre `comercial.temas`; recebeu `CREATE` temporariamente para a aplicacao.

Prefira que o proprietario `postgres` execute os scripts. Se a politica operacional permitir delegacao temporaria, o DBA deve conceder apenas os privilegios necessarios e revoga-los depois da aplicacao.

## Estado de aplicacao

Os scripts `002` a `005` foram aplicados com sucesso no banco `2026` em 8/9/2026 e auditados apos o `COMMIT`. As quatro tabelas foram criadas vazias. Revogue o privilegio temporario com uma conta autorizada:

```sql
REVOKE CREATE ON SCHEMA comercial FROM codex;
```
