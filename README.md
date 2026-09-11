# Cipolatti API

API ASP.NET Core 10 com PostgreSQL e Dapper.

## Configuração

Segredos não devem ser gravados em `appsettings.json`. Configure-os por variáveis de ambiente ou pelo Secret Manager:

```bash
dotnet user-secrets init --project Cipolatti.API
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=...;Database=...;Username=...;Password=..." --project Cipolatti.API
dotnet user-secrets set "Jwt:SigningKey" "uma-chave-aleatoria-com-no-minimo-32-bytes" --project Cipolatti.API
```

As configurações `Jwt:Issuer`, `Jwt:Audience` e `Printer:Connection` também podem ser sobrescritas por ambiente. Todos os endpoints de negócio exigem um JWT válido; somente `/health` é anônimo. Swagger fica disponível apenas em `Development`.

Antes da primeira execução da versão Dapper, aplique [database/001_saida_almox_sequence.sql](database/001_saida_almox_sequence.sql) usando uma conta autorizada. A aplicação deve usar uma conta PostgreSQL própria e com privilégios mínimos.

## Executar

```bash
dotnet restore Cipolatti.API.Tests/Cipolatti.API.Tests.csproj
dotnet run --project Cipolatti.API
```

## Validar

```bash
dotnet build Cipolatti.API/Cipolatti.API.csproj --no-restore
dotnet build Cipolatti.API.Tests/Cipolatti.API.Tests.csproj --no-restore
dotnet test --project Cipolatti.API.Tests/Cipolatti.API.Tests.csproj --no-build
```

## Compatibilidade

As rotas legadas foram preservadas, com estas correções intencionais de contrato:

- listagens extensas agora retornam `{ items, page, pageSize, total }`;
- criação retorna `201`, duplicidade `409`, ausência `404` e atualização bem-sucedida `204`;
- impressão de etiqueta mudou de `GET` para `POST`;
- o `POST` de `Aprovado` foi removido porque a origem é a view `producao.qry_aprovados`.
