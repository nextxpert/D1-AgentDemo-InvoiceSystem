# Invoice Demo Setup

## Local prerequisites

- .NET 10 SDK
- Docker Engine/Desktop for Compose
- `jq` for compact shell output (optional)
- PowerShell 7 and Azure CLI for provisioning/deploy scripts
- A dedicated non-production Microsoft Entra tenant with an administrator authorized to create Agent Identity blueprints and grants

Run the local offline tests and application:

```bash
dotnet test D1.Invoice.sln --configuration Release
pwsh -File ./Run-D1OfflineDemo.ps1
docker compose up --build
```

`Run-D1OfflineDemo.ps1` builds and starts the four services locally, verifies health, runs the autonomous flow and wrong-role checks, verifies the interactive no-role 403, then stops its child processes. It uses a temporary SQLite database and removes successful-run logs/data on exit.

Open `http://localhost:8083/`. Choose Development submitter at `/dev/sign-in`; choose a user without the business role at `/dev/sign-in?submitter=false`. Local offline mode uses no Entra token or secret. It must never be enabled outside Development.

## Azure/Entra objects

| Object | Name | Purpose |
|---|---|---|
| Autonomous blueprint | `D1-Invoice-Workers-Autonomous` | One shared trust/credential boundary for Entry and Approval |
| Entry Agent Identity | `D1-Invoice-Entry-Agent` | App-only invoice create and submission |
| Approval Agent Identity | `D1-Invoice-Approval-Agent` | App-only pending read and approve/reject |
| Interactive blueprint | `D1-Invoice-Assistant-Interactive` | Separate user/OBO trust boundary and access-as-user audience |
| Interactive Agent Identity | `D1-Interactive-Invoice-Assistant` | Delegated invoice-entry actor, no Agent User |
| API app registration | `D1-Synthetic-Invoice-API` | Resource app, application roles, delegated scope |
| Web OIDC client | `D1-Invoice-Web` | Separate server-side confidential client for user sign-in |
| Autonomous UAMI | `id-d1-invoice-workers` | Runtime credential and ACR pull for autonomous worker |
| Interactive UAMI | `id-d1-invoice-interactive` | Separate runtime credential for OBO API |
| API UAMI | `id-d1-invoice-api-runtime` | API image pull and database-secret access |
| Web runtime UAMI | `id-d1-invoice-web-runtime` | Web image pull and Web-client-secret access only |

### API authorization assignments

| Principal | Grant |
|---|---|
| Entry Agent | `Invoice.Create.All` application role |
| Approval Agent | `Invoice.Read.All` and `Invoice.Approve.All` application roles |
| Interactive Agent | `Invoice.Create.Delegated` delegated permission on the API |
| Interactive user | `InvoiceSubmitter` business role, assigned only to authorized demo users |
| Web client | Pre-authorized/consented for the interactive blueprint `access_as_user` scope |

### Blueprint setup

1. Create the two typed Agent Identity blueprints and their Blueprint Principals; a Blueprint Principal is not automatically created.
2. Add an FIC to each blueprint using its own UAMI principal ID, exact tenant v2 issuer, and `api://AzureADTokenExchange` audience.
3. Create/link the three Agent Identities under their intended parent blueprint. Do not create an Agent User. The Entra script deliberately stops here; rerun it with each child agent's client ID and service-principal object ID to continue.
4. Configure the Interactive blueprint as an OAuth API with identifier URI `api://<interactive-blueprint-client-id>` and the delegated `access_as_user` scope. Preauthorize the Web client as required.
5. Register the Invoice API with roles `Invoice.Create.All`, `Invoice.Read.All`, `Invoice.Approve.All` and delegated scope `Invoice.Create.Delegated`; grant assignments from the table above.
6. Verify app IDs versus service-principal object IDs in assignments. Runtime `WithAgentIdentity` takes the Agent Identity client ID; app role assignment uses its service-principal object ID. The Web client also needs delegated permission declarations and grants for both `access_as_user` on the Interactive blueprint and `Invoice.Create.Delegated` on the Invoice API.
7. Validate one live OBO transaction before enabling the presentation flow: inbound user token audience = Interactive blueprint; downstream OBO token subject = user, actor = Interactive Agent where supported, audience = Invoice API, delegated scope = Invoice.Create.Delegated.

Agent Identity creation, consent, and role grant APIs can vary by tenant rollout. The deployment scripts must stop at explicit manual checkpoints when a creation channel is not supported and export only non-sensitive evidence.

## Configuration

All values below are identifiers or non-secret sample defaults except the Web OIDC credential; pass that secret through secure runtime configuration/Key Vault and never check it into `.env.example`, source, or image layers.

| Variable | Used by | Purpose |
|---|---|---|
| `DEMO_MODE` | all hosts | `offline` locally; `live` in Azure |
| `DEMO_RANDOM_SEED`, `DEMO_BATCH_SIZE`, `DEMO_MIN_AMOUNT`, `DEMO_MAX_AMOUNT` | Worker | deterministic generation and demo limits |
| `AUTONOMOUS_APPROVAL_LIMIT` | API/Worker | approval threshold; higher invoices need manual review |
| `DATABASE_PROVIDER`, `DATABASE_CONNECTION_STRING` | API/Worker | SQLite local or PostgreSQL Azure |
| `TENANT_ID` | all live identity hosts | Entra tenant |
| `AZURE_CLIENT_ID` | Worker | autonomous UAMI client ID |
| `AUTONOMOUS_BLUEPRINT_CLIENT_ID` | Worker | parent autonomous blueprint client ID |
| `ENTRY_AGENT_CLIENT_ID`, `APPROVAL_AGENT_CLIENT_ID` | Worker | child Agent Identity client IDs |
| `INVOICE_API_CLIENT_ID`, `INVOICE_API_AUDIENCE` | API/Worker | protected resource app ID and audience |
| `INTERACTIVE_AZURE_CLIENT_ID` | Interactive API | separate Interactive API UAMI client ID |
| `INTERACTIVE_BLUEPRINT_CLIENT_ID`, `INTERACTIVE_BLUEPRINT_AUDIENCE` | Interactive API/Web | separate interactive blueprint app ID/audience |
| `INTERACTIVE_AGENT_CLIENT_ID` | Interactive API | delegated child Agent Identity client ID |
| `INTERACTIVE_BLUEPRINT_ACCESS_SCOPE` | Web | `api://<blueprint-id>/access_as_user` |
| `INVOICE_CREATE_DELEGATED_SCOPE` | Web/Interactive | `api://<api-id>/Invoice.Create.Delegated` |
| `WEB_CLIENT_ID` | Web | separate OIDC confidential-client app ID |
| `WEB_CLIENT_SECRET` | Web | local/temporary OIDC credential only; secure runtime secret, not a file value |
| `INVOICE_API_BASE_URL` | Worker/Interactive | Invoice API service URL |
| `INVOICE_INTERACTIVE_API_BASE_URL` | Web | Interactive API service URL |
| `INVOICE_WORKER_BASE_URL` | Web | Worker service URL |

For local offline values and placeholder IDs, see `.env.example`. For Azure, use managed identity/FIC for blueprints and securely provision the Web client credential. The deploy script prompts for that credential as a hidden secure string, then stages it in a permission-restricted temporary file for Key Vault upload; it is not written to the repository or image. The generated PostgreSQL admin password is stored in Key Vault and is passed briefly to Azure CLI during server creation.

## Deployment sequence

These scripts are intentionally not run by the implementation workflow. Review the target tenant, subscription, resource group, and costs before invoking them. They default to D1 names, create no T1 resources, and emit non-secret evidence under `deploy/**/evidence/`.

1. Run `deploy/azure/Provision-D1Foundation.ps1` with the target subscription and tenant IDs. It creates the D1 resource group, Log Analytics workspace, Container Apps environment, ACR, and four separate runtime UAMIs. Override `-AcrName` with a globally available lowercase registry name if the default is taken.
2. Run `deploy/entra/Provision-D1Entra.ps1` with the tenant and sponsor/owner/submitter UPNs. It creates the API/Web registrations, blueprints, FICs, app roles, scopes, Web delegated permissions, and emits `entra-foundation.json` before stopping at the child-Agent-Identity checkpoint.
3. In the Entra admin center, create the Entry and Approval Agent Identities under the autonomous blueprint and the Interactive Agent Identity under the interactive blueprint. Record each client ID and service-principal object ID. Create a client secret for `D1-Invoice-Web` and keep it out of files and shell history.
4. Rerun `Provision-D1Entra.ps1` with `-ContinueAfterManualCheckpoint` and the three client/object ID pairs. It assigns the minimum application roles and delegated grants and writes `entra-authorization.json`.
5. Build immutable ACR image digests with `deploy/scripts/Build-D1Images.ps1 -AcrName <acr-name>`. This is the only step that builds and pushes images.
6. Run `deploy/azure/Deploy-D1ContainerApps.ps1` with the target subscription/tenant, the operator's current public IPv4 address, and the Entry/Approval Agent client IDs. Enter the Web client secret at the hidden prompt if it is not already in Key Vault. It creates PostgreSQL, restricts its firewall to Container Apps egress IPs, grants secret-scoped Key Vault access, and deploys the four apps using digest references.

The Entra script derives the production OIDC redirect URI from the Container Apps environment domain. For a custom domain, bind its DNS and certificate first, then pass `-WebProductionRedirectUri https://<host>/signin-oidc` to Entra provisioning and `-WebPublicHost <host>` to the Container Apps deployer. The Web runtime honors forwarded HTTPS headers and filters forwarded hosts to its configured public hostname. After deployment, verify `/healthz` and `/readyz`, sign in, run a live OBO submit, run the autonomous flow, and repeat the wrong-role tests. A successful local offline run is not proof of live OBO claims.

## Azure resource naming

All deployment resources use D1-specific names and a dedicated D1 resource group by default. Foundation reuse is opt-in and must remain within the selected resource group. PostgreSQL firewall access is restricted to the Container Apps outbound IPs after bootstrap; the provided operator IP is not retained as a permanent rule. No T1 defaults are used. No Azure resource deployment has been performed by the implementation session.
