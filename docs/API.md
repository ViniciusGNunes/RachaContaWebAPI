# RachaPix — API Contract (source of truth)

Backend and frontend MUST both follow this file. If a change is needed, update this file
first (in the same commit) and tell the other side via `maestri ask`.

- Backend: ASP.NET Core 9 Web API in `backend/` (solution `backend/RachaPix.sln`, project `backend/src/RachaPix.Api`).
- Frontend: Next.js (App Router, TypeScript, Tailwind) in `frontend/`.
- Backend dev URL: `http://localhost:5080`. Frontend dev URL: `http://localhost:3000`.
- Frontend reads base URL from `NEXT_PUBLIC_API_URL` (default `http://localhost:5080`).
- CORS: backend allows `http://localhost:3000`.
- All routes are prefixed with `/api`. JSON uses **camelCase**. Enums are serialized as **strings**.
- Money: `decimal` in C#, `number` in JSON, always 2 decimal places (BRL). Dates: ISO-8601 UTC strings.
- IDs: `Guid` (string in JSON). Public split links use a short `slug` (8 chars, URL-safe, e.g. `k3Fx9aQz`).
- Errors: RFC 7807 `ProblemDetails` (`{ title, status, detail, errors? }`). 400 for validation, 404 not found, 403 bad admin token.
- No user authentication for now. Split admin access is via a secret `adminToken`.

---

## Module 1 — Expense Divider ("Rachar")

Someone pays a bill, creates a split with the total, the participants and their Pix key.
The app generates (a) a **public link** participants open, pick their name, and get a Pix QR
code + "Pix copia e cola" code with their share pre-filled, and (b) an **admin dashboard**
link showing who has paid.

### Pix payload
Backend generates a static Pix **BR Code** (EMV MPM) string per participant:
- `00` payload format `01`; `26` merchant account info (`00`=`br.gov.bcb.pix`, `01`=pix key,
  optional `02`=description, max 72 chars total for 26 contents… keep description short);
  `52`=`0000`; `53`=`986`; `54`=amount (e.g. `25.50`); `58`=`BR`; `59`=receiver name
  (max 25, uppercase, no accents); `60`=receiver city (max 15, uppercase, no accents);
  `62` additional data with `05`=txid (max 25 alnum, use `***` if none — we use a sanitized
  `RC{slug}{index}`); `63`=CRC16-CCITT-FALSE (poly 0x1021, init 0xFFFF) as 4 uppercase hex.
- Pix key types to accept: CPF, CNPJ, email, phone (`+55...`), random key (EVP UUID).
  Normalize: CPF/CNPJ digits only; phone to `+55DDDNNNNNNNNN`; email lowercased.
- The frontend renders the QR image from `pixCode` (client-side, e.g. `qrcode.react`).
  Backend does not return images.

### Split rules
- `splitMode`: `Equal` | `Custom`.
  - `Equal`: total / n, rounded down to cents; leftover cents go to the first participants
    one by one so the sum equals the total exactly.
  - `Custom`: each participant has `amount`; sum must equal `totalAmount` (±0.00), else 400.
- 2..50 participants, unique names (case-insensitive, trimmed). `totalAmount` > 0, ≤ 1,000,000.
- `includeOwner` (bool): if the creator is also in the list (they don't pay themselves) they
  appear as a participant with `isOwner: true`; their share is counted in the split but marked
  `paid: true` automatically and no Pix code is generated for them.

### Endpoints

`POST /api/splits` → 201
```jsonc
// request
{
  "title": "Churrasco de sábado",          // 1..80
  "totalAmount": 300.00,
  "pixKey": "fulano@email.com",
  "pixKeyType": "Email",                   // Cpf | Cnpj | Email | Phone | Random
  "receiverName": "Fulano de Tal",         // 1..25 after sanitization
  "receiverCity": "Sao Paulo",             // 1..15 after sanitization
  "splitMode": "Equal",                    // Equal | Custom
  "participants": [
    { "name": "Fulano", "isOwner": true },
    { "name": "Ciclano", "amount": null },
    { "name": "Beltrano" }
  ]
}
// response: SplitAdminDto (see below), includes adminToken ONLY here
```

`GET /api/splits/{slug}` → 200 `SplitPublicDto`
```jsonc
{
  "slug": "k3Fx9aQz",
  "title": "Churrasco de sábado",
  "totalAmount": 300.00,
  "receiverName": "Fulano de Tal",
  "createdAt": "2026-10-06T18:00:00Z",
  "participants": [
    { "id": "guid", "name": "Fulano", "amount": 100.00, "isOwner": true,  "paid": true,  "paymentPending": false },
    { "id": "guid", "name": "Ciclano", "amount": 100.00, "isOwner": false, "paid": false, "paymentPending": true }
  ],
  "paidCount": 1,          // only admin-confirmed payments
  "paidAmount": 100.00,
  "pendingCount": 1        // participants who claimed payment, awaiting admin confirmation
}
```

`GET /api/splits/{slug}/participants/{participantId}/pix` → 200
```jsonc
{ "participantId": "guid", "name": "Ciclano", "amount": 100.00,
  "pixKey": "fulano@email.com", "receiverName": "FULANO DE TAL", "pixCode": "000201...6304ABCD" }
```
404 for owner participant (no pix needed) — frontend shows "você é o organizador".

`POST /api/splits/{slug}/participants/{participantId}/mark-paid` → 200 `SplitPublicDto`
(participant self-declares payment; idempotent). Body: `{}`.
Sets `paymentPending = true`; the payment only counts (`paid = true`) after the admin confirms it.
No-op if the participant is already `paid`.

`GET /api/splits/{slug}/admin?token={adminToken}` → 200 `SplitAdminDto`
```jsonc
{ /* all SplitPublicDto fields */,
  "pixKey": "...", "pixKeyType": "Email", "receiverCity": "SAO PAULO",
  "adminToken": "..." /* only in POST response; null here */,
  "participants": [ { /* ... */ "paidAt": "2026-10-06T19:00:00Z" | null,
                      "paymentPending": false, "claimedAt": "2026-10-06T18:55:00Z" | null } ] }
```

`POST /api/splits/{slug}/admin/participants/{participantId}/toggle-paid?token=...` → 200 `SplitAdminDto`
(admin confirms a payment, including a pending claim, or reverts a confirmed one; clears `paymentPending`).

`POST /api/splits/{slug}/admin/participants/{participantId}/reject-claim?token=...` → 200 `SplitAdminDto`
(admin dismisses a pending claim without marking it paid).

`DELETE /api/splits/{slug}?token=...` → 204.

### Frontend routes (Module 1)
- `/` landing with two cards: "Rachar uma conta" and "Gerenciar despesas".
- `/rachar` — form (also openable as a modal from `/`): title, total, pix key + type,
  receiver name/city, participants list (add/remove), equal/custom split with live preview.
  On success shows both links (copy buttons) and stores `{slug, adminToken, title}` in
  localStorage list "Meus rachas".
- `/r/{slug}` — public page: title, total, progress; list of names → click "Sou eu" →
  shows QR code + copia-e-cola (copy button) + amount + "Já paguei" button.
- `/r/{slug}/admin?token=...` — dashboard: progress bar, table of participants with status,
  toggle paid, share link, delete split. Auto-refresh every 10s.

---

## Module 2 — Expense Manager ("Despesas")

Persistent groups (e.g. a house, a trip) with members, expenses with payment method and
category, per-member shares, settlements, and balances.

### Enums
- `PaymentMethod`: `Pix` | `Cash` | `CreditCard` | `DebitCard` | `BankTransfer` | `MealVoucher` | `Other`
- `ExpenseCategory`: `Food` | `Groceries` | `Housing` | `Utilities` | `Transport` | `Entertainment` | `Health` | `Travel` | `Shopping` | `Other`
- `ShareMode`: `Equal` | `Custom` | `Percentage`

### DTOs
```jsonc
// GroupSummaryDto
{ "id": "guid", "name": "Apê 42", "description": "...", "currency": "BRL",
  "memberCount": 3, "totalSpent": 1234.56, "createdAt": "..." }

// GroupDto
{ "id": "guid", "name": "...", "description": "...", "currency": "BRL", "createdAt": "...",
  "members": [ MemberDto ] }

// MemberDto
{ "id": "guid", "name": "Ana", "pixKey": "ana@x.com" | null, "pixKeyType": "Email" | null }

// ExpenseDto
{ "id": "guid", "groupId": "guid", "description": "Mercado", "amount": 210.30,
  "date": "2026-10-01", /* date only, yyyy-MM-dd */
  "category": "Groceries", "paymentMethod": "CreditCard",
  "paidByMemberId": "guid", "paidByName": "Ana",
  "shareMode": "Equal",
  "shares": [ { "memberId": "guid", "memberName": "Ana", "amount": 70.10, "percentage": null } ],
  "notes": null, "createdAt": "..." }

// SettlementDto  (a payment between members to clear debt)
{ "id": "guid", "groupId": "guid", "fromMemberId": "guid", "fromName": "Bruno",
  "toMemberId": "guid", "toName": "Ana", "amount": 50.00, "date": "2026-10-02",
  "paymentMethod": "Pix", "notes": null, "createdAt": "..." }

// BalanceDto
{ "groupId": "guid",
  "members": [ { "memberId": "guid", "name": "Ana",
                 "paid": 500.00,      // sum of expenses this member paid
                 "owed": 300.00,      // sum of this member's shares
                 "settlementsSent": 0.00, "settlementsReceived": 50.00,
                 "net": 150.00 } ],   // paid - owed + sent - received; >0 = should receive
  "suggestedTransfers": [ { "fromMemberId": "guid", "fromName": "Bruno",
                            "toMemberId": "guid", "toName": "Ana", "amount": 75.00 } ] }

// StatsDto
{ "groupId": "guid", "totalSpent": 1234.56,
  "byCategory": [ { "category": "Food", "total": 300.00 } ],
  "byPaymentMethod": [ { "paymentMethod": "Pix", "total": 200.00 } ],
  "byMember": [ { "memberId": "guid", "name": "Ana", "paid": 500.00, "owed": 300.00 } ],
  "byMonth": [ { "month": "2026-10", "total": 800.00 } ] }
```

### Rules
- Share computation (server-side, source of truth):
  - `Equal`: request lists `memberIds` participating; split like Module 1 (cents to first).
  - `Custom`: request lists `{memberId, amount}`; sum must equal `amount`.
  - `Percentage`: request lists `{memberId, percentage}`; sum must be 100 (±0.01); amounts
    rounded to cents, remainder to first.
- `suggestedTransfers`: greedy min-cash-flow (largest debtor pays largest creditor).
- A member cannot be deleted while referenced by expenses/settlements (409).
- Filtering on expense list: `?from=yyyy-MM-dd&to=yyyy-MM-dd&category=&paymentMethod=&memberId=`.

### Access control
Groups have no accounts. Each group has two secret tokens, generated at creation:

- `accessToken`: read and add data (members, expenses, settlements, balances, stats, Pix codes).
- `adminToken`: everything above, plus rename/delete the group and remove members.

Every `/api/groups/{id}/...` request must send the token in the `X-Group-Token` header.
A missing or wrong token (and an unknown group) returns **404**, so group ids cannot be probed.
There is no public group listing; clients keep the tokens they were given and use `POST /api/groups/lookup`.
The share link is `/despesas/{id}#t={accessToken}`; the fragment is never sent to the server.

### Endpoints
| Method | Route | Body | Response |
|---|---|---|---|
| POST | `/api/groups/lookup` | `[{ id, token }]` (max 100) | `MyGroupDto[]` (only groups the token unlocks, with `isAdmin`) |
| POST | `/api/groups` | `{ name, description?, members: [{ name, pixKey?, pixKeyType? }] }` | 201 `GroupDto` incl. `accessToken` and `adminToken` (returned only here) |
| GET | `/api/groups/{id}` | – | `GroupDto` |
| PUT | `/api/groups/{id}` | `{ name, description? }` | `GroupDto` (admin) |
| DELETE | `/api/groups/{id}` | – | 204 (cascades, admin) |
| POST | `/api/groups/{id}/members` | `{ name, pixKey?, pixKeyType? }` | 201 `MemberDto` |
| PUT | `/api/groups/{id}/members/{memberId}` | same | `MemberDto` |
| DELETE | `/api/groups/{id}/members/{memberId}` | – | 204 / 409 (admin) |
| GET | `/api/groups/{id}/expenses` | filters | `ExpenseDto[]` (date desc) |
| POST | `/api/groups/{id}/expenses` | see below | 201 `ExpenseDto` |
| GET | `/api/groups/{id}/expenses/{expenseId}` | – | `ExpenseDto` |
| PUT | `/api/groups/{id}/expenses/{expenseId}` | see below | `ExpenseDto` |
| DELETE | `/api/groups/{id}/expenses/{expenseId}` | – | 204 |
| GET | `/api/groups/{id}/settlements` | – | `SettlementDto[]` |
| POST | `/api/groups/{id}/settlements` | `{ fromMemberId, toMemberId, amount, date, paymentMethod, notes? }` | 201 `SettlementDto` |
| DELETE | `/api/groups/{id}/settlements/{settlementId}` | – | 204 |
| GET | `/api/groups/{id}/balances` | – | `BalanceDto` |
| GET | `/api/groups/{id}/stats` | `?from&to` | `StatsDto` |
| GET | `/api/groups/{id}/members/{memberId}/pix?amount=50.00` | – | `{ pixCode, amount, name, pixKey }` (400 if member has no pix key) |

### Devices (same member on several devices)
A member can be used on more than one device. Each device holds its own personal token (stored hashed in `MemberDevices`).

| Method | Route | Body | Response |
|---|---|---|---|
| POST | `/api/groups/{id}/members/me/link-codes` | – | `{ code, expiresAt }` (8 chars, single use, valid 5 min; replaces any pending code; needs a member token) |
| POST | `/api/groups/{id}/link-codes/redeem` | `{ code, label? }` + invite/admin token in `X-Group-Token` | `{ memberToken, member }` (new device token for the member that made the code; 404 for wrong, expired or used codes; rate limited to 5/min per client and group, then 429) |
| GET | `/api/groups/{id}/members/me/devices` | – | `DeviceDto[]` `{ id, label, createdAt, lastUsedAt, isCurrent }` |
| DELETE | `/api/groups/{id}/members/me/devices/{deviceId}` | – | 204 (409 if it is the only device) |

`POST /api/groups/{id}/claim` also accepts an optional `label` for the first device. An admin "reset claim" removes every device and pending code of the member.
The device link shared by the app is `/despesas/group?id={id}#t={inviteToken}&link={code}`.

Expense create/update body:
```jsonc
{ "description": "Mercado", "amount": 210.30, "date": "2026-10-01",
  "category": "Groceries", "paymentMethod": "CreditCard", "paidByMemberId": "guid",
  "shareMode": "Equal",            // Equal | Custom | Percentage
  "shares": [ { "memberId": "guid", "amount": null, "percentage": null } ],
  "notes": null }
```

### Frontend routes (Module 2)
- `/despesas` — list of groups + create group (modal with members).
- `/despesas/{groupId}` — tabs: **Despesas** (list + filters + add/edit modal),
  **Saldos** (balance per member, suggested transfers with "Registrar pagamento" which
  creates a settlement, and a Pix QR for the creditor when they have a pix key),
  **Acertos** (settlements list), **Resumo** (charts by category / payment method / member /
  month), **Membros** (add/edit/remove).

---

## Database
- EF Core 9 + SQLite (`Data Source=rachaconta.db`), connection string in
  `appsettings.json` → `ConnectionStrings:Default`. Provider chosen via `Database:Provider`
  (`Sqlite` now, `Postgres` later) — keep entity config provider-agnostic (no SQLite-only
  SQL; store decimals with `HasPrecision(18,2)`; use `DateTime` UTC and `DateOnly`).
- Migrations live in `backend/src/RachaPix.Api/Migrations`. App applies migrations on
  startup in Development.
