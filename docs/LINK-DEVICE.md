# Vincular outro aparelho à mesma identidade

Proposta para permitir usar o mesmo membro de um grupo em mais de um aparelho (ex.: celular + computador) sem precisar do admin.

## Problema atual

- Ao escolher "Quem é você?", a API gera um **token pessoal** e devolve uma única vez (`ClaimAsync`, [GroupService.cs:89](../src/RachaPix.Api/Services/GroupService.cs#L89)).
- O token só fica no `localStorage` do navegador. No banco fica apenas o hash (`Members.TokenHash`), então **ninguém consegue recuperá-lo**.
- Um membro só pode ser reivindicado uma vez. Para usar em outro aparelho hoje é preciso o admin clicar em "Liberar acesso" (`ResetClaimAsync`), o que derruba o aparelho antigo.

## Ideia

O aparelho **já autenticado** (A) gera um código de uso único e curto. O aparelho **novo** (B) resgata esse código e recebe **seu próprio token** para o mesmo membro. Os dois aparelhos passam a funcionar ao mesmo tempo, e cada um pode ser revogado separadamente.

```
Aparelho A (logado)                API                      Aparelho B (novo)
       |                            |                              |
  1. "Conectar outro aparelho"      |                              |
       |-- POST link-codes -------->|                              |
       |<-- { code, expiresAt } ----|  (guarda só o hash, 5 min)   |
       |                            |                              |
  2. Mostra QR / link com o código  |                              |
       |............ usuário abre o link / lê o QR ...............>|
       |                            |                              |
       |                            |<-- POST link-codes/redeem ---|
       |                            |    { code }                  |
       |                            |--> valida, consome o código  |
       |                            |--> cria token novo p/ membro |
       |                            |---- { token, member } ------>|
       |                            |                      3. salva no localStorage
```

## Mudanças na API

### Modelo de dados (nova migration)

Hoje existe um único `Members.TokenHash`. Para ter vários aparelhos, o token passa para uma tabela própria.

| Tabela | Colunas |
|---|---|
| `MemberDevices` | `Id`, `MemberId` (FK), `TokenHash` (único, indexado), `Label` (ex.: "Chrome - Windows"), `CreatedAt`, `LastUsedAt` |
| `MemberLinkCodes` | `Id`, `MemberId` (FK), `CodeHash`, `ExpiresAt`, `UsedAt` (nulo até o uso) |

- `Members.TokenHash` e `ClaimedAt` continuam existindo durante a transição. A migration copia o `TokenHash` atual de cada membro para um registro em `MemberDevices`, e os membros já reivindicados seguem funcionando.
- `Members.ClaimedAt` passa a significar "tem pelo menos um aparelho".

### Endpoints

| Método e rota | Quem chama | O que faz |
|---|---|---|
| `POST /api/groups/{id}/members/me/link-codes` | Aparelho A (token de membro) | Gera código aleatório (ex.: 8 caracteres, alfabeto sem ambiguidade), salva o hash com validade de **5 minutos** e devolve `{ code, expiresAt }`. |
| `POST /api/groups/{id}/link-codes/redeem` | Aparelho B (token de convite) | Recebe `{ code }`, valida hash, validade e `UsedAt == null`. Marca como usado de forma atômica, cria um `MemberDevices` novo e devolve `{ token, member }`. |
| `GET /api/groups/{id}/members/me/devices` | Aparelho A | Lista os aparelhos vinculados. |
| `DELETE /api/groups/{id}/members/me/devices/{deviceId}` | Aparelho A | Revoga um aparelho (perdeu o celular, por exemplo). |

Detalhes:

- **Resgate atômico:** igual ao `ClaimAsync`, usar `ExecuteUpdateAsync ... Where UsedAt == null && ExpiresAt > now`. Se `rows == 0`, responder 404 genérico, sem dizer se o código estava errado, expirado ou usado.
- **`GroupAccessAttribute`** ([GroupAccessAttribute.cs](../src/RachaPix.Api/Controllers/GroupAccessAttribute.cs)): hoje procura o token em `Members.TokenHash`. Passa a procurar em `MemberDevices.TokenHash` e atualizar `LastUsedAt`. O resgate usa o modo `ClaimOnly`, que já aceita só token de convite ou admin.
- **Admin:** se o membro for admin, o aparelho novo herda `IsAdmin` do `Member`, sem mudança.
- **`ResetClaimAsync`** passa a apagar todos os `MemberDevices` do membro e os códigos pendentes.

### Segurança

- O código é de uso único, curto e expira em 5 minutos. Só o hash é guardado.
- Limitar tentativas de resgate (ex.: 5 por minuto por IP e por grupo), para impedir adivinhação. Com 8 caracteres de um alfabeto de 32 símbolos já são ~40 bits, e somados à validade curta e ao limite de tentativas o risco é baixo.
- Gerar código novo invalida os anteriores ainda pendentes do mesmo membro.
- O token do aparelho novo é gerado pelo servidor e nunca trafega no link. O link carrega só o código.

## Mudanças no front

1. **Tela do grupo, aba de membro ou perfil:** botão "Conectar outro aparelho". Chama `link-codes` e exibe:
   - um **QR code** (a biblioteca `qrcode.react` já está no projeto);
   - o **link** com o código e um botão de copiar;
   - contagem regressiva da validade.
2. **Nova rota de resgate**, ex.: `/despesas/group?id=<id>&invite=<token>&link=<code>`. O app é exportação estática, então o código viaja na query string, como os outros parâmetros em [routes.ts](../../racha-conta-front/src/lib/routes.ts). Ao abrir:
   - detecta `link` na URL;
   - chama `redeem` usando o token de convite como `X-Group-Token`;
   - grava `memberToken` e `memberId` via `saveGroup` ([storage.ts](../../racha-conta-front/src/lib/storage.ts)) e entra no grupo já identificado, sem passar por "Quem é você?";
   - remove o código da URL com `history.replaceState`.
3. **Lista de aparelhos** (opcional, mas recomendada): mostra "Este aparelho", os outros, a última utilização e o botão "Desconectar".
4. **`IdentityPicker`:** adicionar o texto "Já tem uma conta em outro aparelho? Peça um código de conexão a ele".

## Fluxo do usuário

1. No celular, em um grupo onde já escolheu seu nome, toque em **Conectar outro aparelho**.
2. No computador, abra o link do grupo e leia o QR (ou cole o link recebido por mensagem).
3. O computador entra já como você. O celular continua logado.
4. Se perder um aparelho, abra a lista de aparelhos no outro e desconecte o perdido.

## Casos de borda

| Situação | Comportamento |
|---|---|
| Código expirado ou já usado | 404 genérico; o front mostra "Código inválido ou expirado. Gere outro no aparelho conectado." |
| Aparelho B já tem identidade no grupo | 409; o front pede para desconectar antes ou ignora. |
| Link de convite do grupo foi trocado (`rotate-invite`) | O resgate falha. Gerar o QR novamente depois que o convite estiver estável, ou incluir o token de convite atual no link. |
| Perdeu o único aparelho | Continua valendo o fluxo atual: o admin usa "Liberar acesso" e o membro escolhe o nome de novo. |
| Membro excluído | `MemberDevices` e códigos apagados em cascata. |

## Plano de implementação

1. Migration com `MemberDevices` e `MemberLinkCodes`, mais cópia dos tokens atuais.
2. Ajustar `ClaimAsync`, `ResetClaimAsync` e `GroupAccessAttribute` para usar `MemberDevices`.
3. Endpoints de gerar código, resgatar, listar e revogar aparelhos, com rate limit.
4. Testes: resgate feliz, código reutilizado, código expirado, resgate concorrente (dois aparelhos com o mesmo código), revogação, reset do admin. Em [ServiceTests.cs](../tests/RachaPix.Tests/ServiceTests.cs).
5. Front: tela de conexão (QR + link), rota de resgate, lista de aparelhos.
6. Documentar os endpoints novos em [API.md](API.md).

## Alternativa mais simples (não recomendada)

"Transferir" em vez de "adicionar": o aparelho A gera o código e, ao resgatar, o servidor **troca** `Members.TokenHash` por um token novo, derrubando o aparelho A. Não precisa de `MemberDevices`, mas só um aparelho funciona por vez, que é praticamente o que o "Liberar acesso" do admin já faz, sem depender dele.
