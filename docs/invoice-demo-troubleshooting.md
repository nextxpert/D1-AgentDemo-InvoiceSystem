# Invoice Demo Troubleshooting

## Web sign-in returns HTTP 500

If `/healthz` succeeds but `/sign-in` fails, inspect Web console logs before changing roles or secrets. `IDX20803` / `IDX20807` referencing `https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/...` means OIDC is using the placeholder tenant, not an invalid user password or failed agent permission.

The Web host maps `TENANT_ID` into `AzureAd:TenantId` and rejects missing/zero tenant GUIDs in live mode. Older Web images did not perform that mapping even when deployment supplied the correct `TENANT_ID`. For an older deployed image, an approved configuration-only workaround is to add `AzureAd__TenantId` with the same real tenant GUID under **invoice-web > Containers > Environment variables**, preserving every existing setting and secret reference. This creates a new Web revision; wait for it to be healthy and receiving traffic before retrying.

Rebuild and deploy the corrected Web image for the permanent code fix. Confirm `/sign-in` returns HTTP 302 to the real tenant's Microsoft Entra authorization endpoint, with the deployed HTTPS `/signin-oidc` callback. This verifies the sign-in challenge only; user sign-in, consent, interactive OBO, and API authorization still need separate validation. Never enable offline mode in Azure to bypass the failure.

### Callback fails with AADSTS28000

If login reaches `/signin-oidc` and then returns 500, check for `AADSTS28000: ... scope ... contains more than one resource`. Initial sign-in must request only the Interactive blueprint's `access_as_user` resource scope, alongside standard OIDC scopes. Do not include `Invoice.Create.Delegated` for the separate Invoice API in the same authorization-code redemption.

The Web client acquires the Invoice API token separately when the user triggers a worker command; retain the Web client's consent for that API and the `INVOICE_CREATE_DELEGATED_SCOPE` setting. Deploy the corrected Web image, then start a fresh `/sign-in` flow in a private browser window. Refreshing or replaying the failed `/signin-oidc` callback cannot retry a completed authorization-code exchange safely.

## 401 versus 403

- **401** means the API could not authenticate the request: missing bearer token, bad signature, wrong issuer/tenant, expired token, or wrong audience. Confirm the web token audience is the interactive blueprint for the first hop and the OBO token audience is the Invoice API for the downstream hop.
- **403** means authentication succeeded but authorization failed. Check the exact application role (`Invoice.Create.All`, `Invoice.Read.All`, `Invoice.Approve.All`), delegated scope (`Invoice.Create.Delegated`), user business role (`InvoiceSubmitter`), and agent context. A valid wrong-role token must remain 403.
- Offline mode uses `X-Demo-Actor`; it is Development-only, synthetic, and not evidence of a live token.

If the Invoice API logs `HasDelegatedScope=True HasSubmitterRole=True HasAgentActor=False HasAgentIdentityFacet=False`, check the **Invoice API resource registration's** `optionalClaims.accessToken`. It must request `xms_act_fct` so Entra emits the agent actor facet (`11`) for an Agent Identity. Entra provisioning configures this claim while preserving existing optional claims. After correcting the registration, restart the Interactive revision to clear its in-memory downstream token cache, then retry Preview before Submit. Do not remove the actor check or accept an ordinary `azp` as agent evidence. See [Microsoft's agent token claims reference](https://learn.microsoft.com/entra/agent-id/agent-token-claims).

## Autonomous Agent ID / FIC

Check that `AZURE_CLIENT_ID` selects the UAMI attached to `Invoice.Worker`; the FIC is on `D1-Invoice-Workers-Autonomous`; its issuer is the tenant’s v2 issuer; its subject is the UAMI **principal ID**; and its audience is `api://AzureADTokenExchange`. `fmi_path` uses the child agent **client ID**, not the service principal object ID.

Entry should carry only `Invoice.Create.All`. Approval should carry only `Invoice.Read.All` and `Invoice.Approve.All`. Recheck each role assignment on the Invoice API service principal if the token is valid but gets 403.

## Interactive OBO

1. Confirm the Web confidential client is separate from the interactive blueprint and has the correct callback URL.
2. The Web client must request `api://<interactive-blueprint-client-id>/access_as_user`; it must not request the Invoice API token as its initial sign-in audience.
3. Confirm the interactive blueprint has an enabled `access_as_user` scope and preauthorizes the Web client as required by the tenant configuration.
4. Confirm `Invoice.Interactive` validates the user token against the blueprint audience and uses the separate Interactive API UAMI/FIC.
5. Confirm the interactive Agent Identity has delegated consent for `Invoice.Create.Delegated` on the Invoice API.
6. Verify the downstream token represents the signed-in user using matching `oid` and `tid`, and verify agent context is present. Entra `sub` is audience-specific, so comparing subjects from the Interactive blueprint and Invoice API can incorrectly reject valid OBO tokens. If logs say "OBO token subject does not match the signed-in user", deploy the corrected Interactive image; do not remove the identity check. Missing or mismatched tenant/object IDs must remain denied. Never substitute a synthetic actor for live validation.
7. Check `InvoiceSubmitter` is present on the downstream delegated token/user authorization path. The API makes the final decision.

## Database and invoice behavior

- SQLite Development uses `EnsureCreated`; PostgreSQL live startup uses the checked-in EF migration. Do not use PostgreSQL migrations against a SQLite database.
- If startup reports pending model changes, compare the EF model snapshot and migration, regenerate the PostgreSQL migration, and rerun the solution tests.
- A duplicate number returns 409; unique indexes remain the final guard in concurrent requests.
- Over-limit approval remains PendingApproval with `ManualReviewRequired`; it is not an error and no payment is initiated.
- `DateTimeOffset` ordering is materialized before sorting for SQLite compatibility.

## Containers and ports

- In Azure Container Apps, callers must use the destination app's HTTPS ingress URL, such as `https://invoice-api.internal.<environment-domain>/`. The container listens on port 8080, but callers do not connect directly to that port through the ingress hostname. The Compose URL `http://invoice-api:8080/` is for local rehearsal only.
- If the Web invoice list times out after 100 seconds, inspect its HttpClient destination before increasing timeouts. Verify `INVOICE_INTERACTIVE_API_BASE_URL` and `INVOICE_WORKER_BASE_URL` use the respective internal HTTPS ingress hostnames; verify `INVOICE_API_BASE_URL` on Interactive and Worker uses the API's internal HTTPS hostname. Preserve internal-only ingress and existing token authorization. A configuration update creates new revisions and requires fresh sign-in if the Web in-memory token cache was restarted.
- API: port 8080, health `/healthz`, readiness `/readyz`.
- Worker: port 8080, health `/healthz`, readiness `/readyz`.
- Interactive API: port 8080.
- Web: port 8080.
- Compose host ports: 8080, 8081, 8082, 8083 respectively. Check `depends_on` health and container logs if a caller sees connection refused.
- All images use multi-stage builds and non-root runtime. A build must fail if tests fail.

### SQLite volume permissions

If the API is unhealthy and its logs show `SQLite Error 14: 'unable to open database file'`,
the shared `/data` volume may be owned by root from an older image. The API and Worker
images initialize new volumes with ownership matching their non-root runtime user.
Rebuilding does not change ownership on an existing volume.

Repair the existing default Compose volume without deleting its database:

```bash
docker compose stop
docker run --rm --user 0 \
  --mount type=volume,src=d1-invoice-demo_invoice-data,dst=/data \
  mcr.microsoft.com/dotnet/sdk:10.0-noble \
  sh -c 'chown -R "$APP_UID:$APP_UID" /data'
docker compose up --build -d --wait
```

If you override the Compose project name, use the actual volume name shown by
`docker volume ls` instead. Do not use `docker compose down -v` unless you intend
to delete the stored demo data.

## Isolation tests and token caching

Agent Identity tokens can be cached in process. After disabling Entry or Approval, restart the worker so a cached token cannot mask the kill switch; allow Entra propagation. Confirm the other agent still succeeds, then re-enable and restart. Never leave a demo agent disabled after rehearsal.

## Secret/token hygiene

If a token, authorization header, client secret, or certificate value appears in a log, response, screenshot, or committed file, stop the demo, rotate/revoke it, remove it from retained artifacts, and review logging. Only allow-listed safe claim summaries may be displayed.
