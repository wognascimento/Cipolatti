# Fotos de romaneio no Expo

O token de dispositivo deve ser recebido uma única vez pela rota de ativação e armazenado no `expo-secure-store`. Não o grave em `.env`, `AsyncStorage` ou no código-fonte.

## Ativação

```ts
import * as SecureStore from "expo-secure-store";

const response = await fetch(`${apiUrl}/api/Romaneio/dispositivos/ativar`, {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ activationCode }),
});

if (!response.ok) throw new Error("Código de ativação inválido ou expirado.");
const activation = await response.json();
await SecureStore.setItemAsync("romaneio_device_token", activation.deviceToken);
```

O código de ativação é criado por um administrador em `POST /api/Romaneio/dispositivos`, expira em 30 minutos e só pode ser utilizado uma vez.

## Upload

```ts
import * as Crypto from "expo-crypto";
import * as SecureStore from "expo-secure-store";

const token = await SecureStore.getItemAsync("romaneio_device_token");
if (!token) throw new Error("Este aparelho ainda não foi ativado.");

const form = new FormData();
for (const [index, photo] of photos.entries()) {
  form.append("fotos", {
    uri: photo.uri,
    name: `foto-${index + 1}.jpg`,
    type: photo.mimeType ?? "image/jpeg",
  } as never);
}
form.append("observacao", observacao ?? "");

const idempotencyKey = Crypto.randomUUID();
const response = await fetch(`${apiUrl}/api/Romaneio/${codigo}/finalizar-fotos`, {
  method: "POST",
  headers: {
    Authorization: `Bearer ${token}`,
    "Idempotency-Key": idempotencyKey,
  },
  body: form,
});
```

Guarde a mesma `Idempotency-Key` até receber sucesso. Se a conexão cair ou o e-mail falhar, repita a requisição com a mesma chave para evitar fotos e notificações duplicadas.

Não defina manualmente o cabeçalho `Content-Type` do upload: o `fetch` adiciona o `boundary` correto do `multipart/form-data`.
