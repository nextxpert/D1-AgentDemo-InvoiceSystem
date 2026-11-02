# Invoice Demo Security

## Trust boundaries

- **Autonomous boundary:** one D1 autonomous blueprint and worker UAMI/FIC trust one shared worker runtime. Entry and Approval are child Agent Identities with independent authorization and lifecycle.
- **Interactive boundary:** separate Web OIDC client, interactive blueprint, interactive child Agent Identity, and Interactive API managed identity/FIC. Do not reuse the autonomous blueprint credential for OBO.
- **Resource boundary:** Invoice API validates issuer, signature, audience, delegated scope/app role, actor context where required, and user business role. The API—not UI state—decides.
- **Persistence boundary:** `Invoice.Application` owns validation/creation. Both Entry and Interactive call it through the protected Invoice API. Approval logic is separate and server-side.

## Least privilege

| Actor | Permissions | Explicit denials |
|---|---|---|
| Entry | `Invoice.Create.All`; create, submit, read own | Approve/reject, modify decided financial data, mark paid |
| Approval | `Invoice.Read.All`, `Invoice.Approve.All`; decide only PendingApproval | Create, change supplier/amount/lines, pay |
| Interactive Assistant | `Invoice.Create.Delegated` on behalf of an authenticated user | Approve/reject, use application permission, bypass user `InvoiceSubmitter` |
| Signed-in user | `InvoiceSubmitter` business role | User role alone never grants approval |

The API requires `Invoice.Create.Delegated` plus `InvoiceSubmitter` and the interactive agent context for delegated creation. Own-invoice reads are additionally filtered to the persisted on-behalf-of user. The worker's live demo routes require a signed-in `InvoiceSubmitter` with the trigger scope.

## Authentication and token handling

- Autonomous token flow uses `Microsoft.Identity.Web.AgentIdentities`, `WithAgentIdentity`, `IAuthorizationHeaderProvider`, and managed identity plus FIC. The agent identity ID is the Agent Identity app/client ID, not its service-principal object ID.
- Interactive flow receives a user token whose audience is the interactive blueprint. The Interactive API calls `CreateAuthorizationHeaderForUserAsync` with the signed-in `ClaimsPrincipal` and `WithAgentIdentity(interactiveAgentId)` to perform OBO.
- The OBO downstream token must represent the signed-in user; the agent actor is captured from the documented claim where present. Entra `sub` is audience-specific and need not be identical between the Interactive blueprint and Invoice API tokens. The Interactive service verifies the same user using matching non-empty `oid` and `tid` claims, while retaining each token's actual subject in safe evidence. The API refuses a missing actor context for delegated create.
- The Web OIDC client secret is supplied only through secure runtime configuration (`WEB_CLIENT_SECRET`) and is never committed. Production should use a managed certificate/Key Vault-backed credential where configured and supported.
- Offline mode uses explicit fake identities/headers in Development only. Production refuses offline mode.
- Safe claims are parsed from token payload into a small allow-listed DTO. The raw token/header is never serialized, logged, persisted, or returned.

## Invoice invariants

- Invoice statuses are Draft, PendingApproval, ApprovedForPayment, and Rejected. No Paid status exists.
- No payment API, bank account field, bank data, or payment execution code exists.
- All suppliers and invoice data are synthetic.
- `InvoiceNumber` and `PurchaseOrderNumber` have unique database indexes and application prechecks.
- Approval requires PendingApproval; the domain prevents the same autonomous identity from creating and approving an invoice.
- Manual review retains PendingApproval with a stored reason; the autonomous approval process skips it.
- Financial lines/totals are calculated server-side; UI values are never trusted.

## Logging and audit

Audit events contain correlation ID, invoice ID, actor/user IDs and names, actor type, operation, required permission, outcome/reason, and time. Token scopes/roles are summarized only as needed. Do not enable HTTP body/header logging in production. SQLite schema/query logs can contain synthetic invoice fields in Development; Production logging should remain at Information/Warning and never log authorization headers.

## Failure semantics

- 401: missing/invalid token or failed authentication.
- 403: valid caller without the endpoint role/scope, missing `InvoiceSubmitter`, missing agent context, or wrong actor.
- 409: duplicate invoice/PO number, invalid state transition, or conflicting business operation.
- No authorization decision is based solely on the UI or a caller-provided identity ID.
