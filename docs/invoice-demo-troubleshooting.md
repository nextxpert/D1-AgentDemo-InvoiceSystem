# Invoice Demo Troubleshooting

## 401 versus 403

- **401** means the API could not authenticate the request: missing bearer token, bad signature, wrong issuer/tenant, expired token, or wrong audience. Confirm the web token audience is the interactive blueprint for the first hop and the OBO token audience is the Invoice API for the downstream hop.
- **403** means authentication succeeded but authorization failed. Check the exact application role (`Invoice.Create.All`, `Invoice.Read.All`, `Invoice.Approve.All`), delegated scope (`Invoice.Create.Delegated`), user business role (`InvoiceSubmitter`), and agent context. A valid wrong-role token must remain 403.
- Offline mode uses `X-Demo-Actor`; it is Development-only, synthetic, and not evidence of a live token.

## Autonomous Agent ID / FIC

Check that `AZURE_CLIENT_ID` selects the UAMI attached to `Invoice.Worker`; the FIC is on `D1-Invoice-Workers-Autonomous`; its issuer is the tenant’s v2 issuer; its subject is the UAMI **principal ID**; and its audience is `api://AzureADTokenExchange`. `fmi_path` uses the child agent **client ID**, not the service principal object ID.

Entry should carry only `Invoice.Create.All`. Approval should carry only `Invoice.Read.All` and `Invoice.Approve.All`. Recheck each role assignment on the Invoice API service principal if the token is valid but gets 403.

## Interactive OBO

1. Confirm the Web confidential client is separate from the interactive blueprint and has the correct callback URL.
2. The Web client must request `api://<interactive-blueprint-client-id>/access_as_user`; it must not request the Invoice API token as its initial sign-in audience.
3. Confirm the interactive blueprint has an enabled `access_as_user` scope and preauthorizes the Web client as required by the tenant configuration.
4. Confirm `Invoice.Interactive` validates the user token against the blueprint audience and uses the separate Interactive API UAMI/FIC.
5. Confirm the interactive Agent Identity has delegated consent for `Invoice.Create.Delegated` on the Invoice API.
6. Verify the downstream token subject is the signed-in user and agent context is present; if not, stop and inspect the Microsoft.Identity.Web OBO setup. Never substitute a synthetic actor for live validation.
7. Check `InvoiceSubmitter` is present on the downstream delegated token/user authorization path. The API makes the final decision.

## Database and invoice behavior

- SQLite Development uses `EnsureCreated`; PostgreSQL live startup uses the checked-in EF migration. Do not use PostgreSQL migrations against a SQLite database.
- If startup reports pending model changes, compare the EF model snapshot and migration, regenerate the PostgreSQL migration, and rerun the solution tests.
- A duplicate number returns 409; unique indexes remain the final guard in concurrent requests.
- Over-limit approval remains PendingApproval with `ManualReviewRequired`; it is not an error and no payment is initiated.
- `DateTimeOffset` ordering is materialized before sorting for SQLite compatibility.

## Containers and ports

- API: port 8080, health `/healthz`, readiness `/readyz`.
- Worker: port 8080, health `/healthz`, readiness `/readyz`.
- Interactive API: port 8080.
- Web: port 8080.
- Compose host ports: 8080, 8081, 8082, 8083 respectively. Check `depends_on` health and container logs if a caller sees connection refused.
- All images use multi-stage builds and non-root runtime. A build must fail if tests fail.

## Isolation tests and token caching

Agent Identity tokens can be cached in process. After disabling Entry or Approval, restart the worker so a cached token cannot mask the kill switch; allow Entra propagation. Confirm the other agent still succeeds, then re-enable and restart. Never leave a demo agent disabled after rehearsal.

## Secret/token hygiene

If a token, authorization header, client secret, or certificate value appears in a log, response, screenshot, or committed file, stop the demo, rotate/revoke it, remove it from retained artifacts, and review logging. Only allow-listed safe claim summaries may be displayed.
