# Invoice Demo Live Script

## Goal

Show one application with a shared autonomous runtime, distinct Entry and Approval agent identities, and a separate interactive agent acting for a signed-in user. Do not show tokens, secrets, real supplier data, or any payment action.

## Local rehearsal (offline)

Start the four services:

```bash
docker compose up --build
```

Open `http://localhost:8083/`. Sign in as the fictional submitter at `/dev/sign-in`; use `/dev/sign-in?submitter=false` for a user without the `InvoiceSubmitter` business role. Offline mode uses fake actors and actor headers only in Development; it is rehearsal mode, not proof of real Entra claims.

Run a deterministic autonomous flow:

```bash
curl -sS -X POST http://localhost:8081/api/demo/runs/autonomous | jq '{status,invoiceCount:(.invoices|length),decisions:[.decisions[]|{disposition,reason}],actors:[.executions[]|{agentName,roles:.claims.roles}]}'
```

Prove wrong-role enforcement:

```bash
curl -sS -X POST http://localhost:8081/api/demo/runs/negative-tests | jq '{status,attempts:[.executions[]|{agentName,operation,outcome,detail}]}'
```

Expected: `D1-Invoice-Approval-Agent` create attempt is `Denied / Forbidden`; `D1-Invoice-Entry-Agent` approval attempt is `Denied / Forbidden`.

## Suggested seven-minute arc

### 0:00 — Problem

Say: “One service principal for the whole application is a familiar pattern. But this application now contains autonomous actors with different duties. If they share one identity, permissions, audit identity, and kill switch become too coarse.”

Show the identity tiles on the dashboard.

### 0:45 — Shared autonomous trust boundary

Show the autonomous blueprint and its managed-identity FIC. Say: “Entry and Approval share code, runtime, deployment, and credential boundary, so they share one autonomous blueprint. That does not mean they share authorization.”

### 1:30 — Separate attribution boundaries

Show `D1-Invoice-Entry-Agent` and `D1-Invoice-Approval-Agent`. Say: “These are different security subjects, not labels in a prompt.”

### 2:15 — Least privilege

Show role assignments: Entry has `Invoice.Create.All`; Approval has `Invoice.Read.All` and `Invoice.Approve.All`. Explain that an application token’s role is enforced by the Invoice API, not by hiding a UI button.

### 3:00 — Autonomous run

Trigger the autonomous run from the dashboard or:

```bash
curl -sS -X POST http://localhost:8081/api/demo/runs/autonomous | jq
```

Show generated fictional invoices, decisions, per-agent safe claim summaries, and one correlation ID. `ManualReviewRequired` remains PendingApproval; nothing is paid.

### 4:00 — Permission wall

Run the negative tests. Say: “A valid Entry token is not an Approval token. The Invoice API rejects Entry’s approve request with 403; it also rejects Approval’s create request with 403.”

### 4:40 — Interactive OBO

Open New invoice and show the signed-in user context, assistant identity, preview totals, and submit result. In live mode, show a user subject plus the interactive agent actor and delegated scope. The user remains the subject; the assistant does not become the user.

Offline mode prints clearly labeled synthetic claims only. Do not present them as Entra evidence.

### 5:40 — User authorization

Sign in as the Development read-only user or use a live user without the `InvoiceSubmitter` role. Submission must be denied by the Invoice API with 403. A user can never approve.

### 6:20 — Four decisions

1. Identity type: Agent Identities for autonomous actors, real user sign-in for interactive work.
2. Operating context: autonomous app permissions versus delegated user permissions.
3. Trust boundary: one autonomous blueprint for one shared runtime/credential boundary.
4. Attribution boundary: distinct child identities and a separate interactive blueprint where lifecycle and user context differ.

Final line: “One application can become a team. Give every accountable actor the identity boundary its responsibilities deserve.”

## Current live-state prerequisites

- D1 Entra API registration and exact roles/scope created and consented.
- Autonomous blueprint FIC points to the autonomous worker UAMI principal ID.
- Interactive blueprint FIC points to its separate Interactive API UAMI principal ID.
- Child Agent Identities created and linked to the correct blueprints.
- App roles assigned to Entry/Approval; delegated Invoice API scope granted to the interactive child agent.
- Web OIDC client registration is pre-authorized for the interactive blueprint scope.
- Web/API/worker/interactive containers use immutable image digests; PostgreSQL is configured for Azure.
- Validate live OBO subject/actor claims, separate role claims, and 401/403 paths before the session.

## Recovery and cleanup

If live identity setup is unavailable, switch to offline mode and explicitly state that claims are synthetic. Keep screenshots of live token summaries/role assignments and wrong-role 403 results as backup. After demo, re-enable any agent disabled for isolation, restart the worker to clear token caches, verify Entry and Approval work, and restore the safe offline mode if required.
