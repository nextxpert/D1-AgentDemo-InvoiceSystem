# Invoice Demo Live Script

## Goal

Show invoices being created, submitted, and approved in the online Web UI. Explain one shared autonomous runtime with distinct Entry and Approval agent identities, and a separate interactive assistant acting for a signed-in user. Use fictional data only. There is no payment action or `Paid` state.

## Open the online invoice URL

The invoice workspace is **the deployed Web app's root URL**, not the Invoice API, worker, or Interactive API URL. There is no separate `/invoices` list page.

From the repository root, use PowerShell to print the actual URLs recorded by deployment:

```powershell
$deployment = Get-Content ./deploy/azure/evidence/container-app-deployment.json -Raw | ConvertFrom-Json
$webUrl = $deployment.WebUrl.TrimEnd('/')
[pscustomobject]@{
	InvoiceWorkspace = "$webUrl/"
	SignIn = "$webUrl/sign-in"
	InteractiveAssistant = "$webUrl/invoices/new"
	SignOut = "$webUrl/sign-out"
	WebHealth = "$webUrl/healthz"
} | Format-List
```

Open the printed **SignIn** URL, then **InvoiceWorkspace**. Bookmark both **InvoiceWorkspace** and **InteractiveAssistant** for the presentation.

If the evidence file is missing, open Azure portal > the deployed D1 resource group > Container Apps > **invoice-web** > Overview > **Application URL**. Use its HTTPS URL, or the configured custom domain, as the base URL above. The deployment script also prints `D1 Container Apps deployment complete: https://...`. Do not guess the environment hostname.

No deployed hostname is checked into this script. If the app has not been deployed, complete the [deployment sequence](invoice-demo-setup.md#deployment-sequence) first. Updating this repository does not update a running Azure revision: rebuild the changed Web/Worker images and deploy their immutable digests through that sequence before presenting these controls. Do not enable offline mode in Azure.

| Browser destination | What to show |
|---|---|
| `<WebUrl>/sign-in` | Real Microsoft Entra user sign-in |
| `<WebUrl>/` | **Invoice workspace**: current user's invoices and statuses; **Run autonomous team**: latest worker result, invoices, decisions, and safe role summaries |
| `<WebUrl>/invoices/new` | Interactive assistant: invoice form, preview, submit, and safe subject/actor evidence |
| `<WebUrl>/sign-out` | End the session before changing demo users |
| `<WebUrl>/healthz` | Web process health only; not proof of working OBO, database access, or permissions |

Only the Web app is public in the supplied deployment. Its server calls the internal worker and Interactive API; they call the protected Invoice API. Do not replace localhost in an anonymous `curl` command with the Web URL: live worker routes require authenticated delegated authorization and are not Web routes.

## Before the audience arrives

1. Complete [Entra configuration and grants](invoice-demo-setup.md#azureentra-objects). Confirm both blueprints and their separate managed-identity FICs, the three child identities, and the Web OIDC redirect URI for the deployed hostname.
2. Verify Entry has `Invoice.Create.All`; Approval has `Invoice.Read.All` and `Invoice.Approve.All`; the interactive child has delegated `Invoice.Create.Delegated`; the Web client has the required consent. Prepare a real demo user with `InvoiceSubmitter` and a second user without it.
3. Confirm all four hosts use `DEMO_MODE=live`, the expected immutable image digests, and PostgreSQL. Verify API/Worker readiness and Interactive API health from an authorized internal operational context; the Web host exposes `/healthz`, not `/readyz`.
4. Open the public Web `/healthz`, sign in as the submitter, and rehearse one preview, submit, autonomous run, approval of pending invoices, and permission-boundary test below. A green health endpoint or offline rehearsal is not live token proof.
5. Check the configured `AUTONOMOUS_APPROVAL_LIMIT` before choosing amounts. The default is 10,000. The examples below assume that default.
6. Prepare two browser profiles for the two users. Keep safe claim/role evidence and correlation IDs from a successful live rehearsal; never display raw JWTs, authorization headers, secrets, or real supplier data.

## Online walkthrough

### 1. Sign in and orient the audience

Open `<WebUrl>/sign-in` and use the real submitter account. Open `<WebUrl>/`.

Show the Entry, Approval, and Interactive identity tiles. Point out **Invoice workspace** and the counters: these are the signed-in user's submissions, not an all-invoice administrator view. An empty table is expected for a new user.

Say: "One application now has a team. Entry creates, Approval decides, and the Interactive Assistant acts for a user. The API enforces their permissions independently."

### 2. Create and approve autonomous invoices

1. Scroll to **Run autonomous team** and click **Run autonomous flow** once. Wait for the controls to become available again.
2. Confirm run status **Succeeded**, mode **live**, a correlation ID, and the invoice/decision counts. The default batch is five; configured batch sizes can differ.
3. Show the execution rows in order: Entry creates and submits using `Invoice.Create.All`; Approval reads pending invoices using `Invoice.Read.All` and evaluates them using `Invoice.Approve.All`.
4. Show the invoice table **inside the run result**, including invoice/PO numbers, fictional supplier, total, status, disposition, and decision reason. These are the generated invoices; they do not appear in the user's owner-filtered **Invoice workspace**.
5. Explain the lifecycle: Entry creates `Draft`, submits to `PendingApproval`, and Approval records a policy decision. This combined button runs both stages, so the table shows the resulting statuses rather than pausing at Draft.

| Outcome | Invoice status | Meaning |
|---|---|---|
| `Approved` | `ApprovedForPayment` | Passed deterministic policy; no payment is executed |
| `ManualReviewRequired` | `PendingApproval` | Above the configured limit or otherwise flagged; remains undecided by automation |
| `Rejected` | `Rejected` | Failed an applicable approval rule; inspect the decision reason |

Do not promise an exact mix of outcomes: amounts and identifiers vary by run. Valid generated invoices need not include a rejection. Each new run persists another batch; this is not a reset button.

Say: "These are distinct security subjects, not prompt labels. Their shared runtime and autonomous blueprint do not give them shared authorization."

### 3. Use the interactive assistant to enter an invoice

Click **New invoice** in the header or **Enter an invoice**, or open `<WebUrl>/invoices/new` directly. This assistant is a **form-based preview and submission workflow**, not a conversational chat agent.

Use this fictional example:

| Field | Value |
|---|---|
| Fictional supplier | Northwind Office Supplies |
| Currency | EUR |
| Invoice number | Keep the generated value, or click its **Generate** button |
| Purchase-order number | Keep the generated value, or click its **Generate** button |
| Invoice date / Due date | Today / 30 days later |
| Cost center | FIN-100 |
| Invoice description | Fictional office supplies invoice |
| Line description | Fictional office supplies |
| Qty / Unit price / Tax % | 2 / 50 / 20 |

1. Click **Preview**. Confirm subtotal **EUR 100.00**, tax **EUR 20.00**, total **EUR 120.00**, and "Preview ready; invoice has not been saved." Preview does not create an invoice.
2. Show the safe evidence next to the preview: **Acting agent**, **Signed-in user subject**, **Permission / scope**, **Result**, and **Correlation ID**. Validate the live downstream scope is `Invoice.Create.Delegated` and the user remains the subject; the acting agent is the interactive child, not Entry or Approval. Do not claim a missing actor field proves OBO; investigate it before presenting live identity evidence.
3. Click **Submit invoice** once. Confirm "Invoice submitted to PendingApproval." Record the invoice number and correlation ID. Submission creates and submits the invoice; there is no approve or pay button on this screen.
4. Click **Overview** to return to `<WebUrl>/`. Click **Refresh** in **Invoice workspace**. Find the recorded invoice number with status **PendingApproval**. The counters and table now include this user's submission.

Say: "The assistant acts for the signed-in user. It does not become the user, and neither the user nor this delegated assistant receives approval permission."

### 4. Approve the interactive submission

1. On Overview, scroll to **Run autonomous team** and click **Approve pending invoices**.
2. Confirm **Succeeded**, mode **live**, Approval execution rows, and a decision for the invoice number recorded above. The EUR 120 example should become **ApprovedForPayment / Approved** under the default policy.
3. Show the decision reason in the run table, then return to **Invoice workspace**. The page refreshes that list after the run; click **Refresh** if needed. The same user-owned invoice should now be **ApprovedForPayment**.

This button asks the worker to use the **Approval Agent's** application permissions. It does not grant the signed-in user or Interactive Assistant an approve permission. It evaluates **all eligible pending invoices**, including other demo users' and prior negative-test invoices, not just the current user's invoice. Use an isolated demo dataset and explain unexpected extra decisions. Already flagged manual-review invoices are skipped.

Do not use **Run autonomous flow** for this step: it creates a new batch and approves only that batch, not the existing interactive submission.

### 5. Show the manual-review boundary

Open **New invoice** again for fresh invoice/PO numbers. Keep the same supplier and dates; set Qty **1**, Unit price **12000**, Tax % **0**. Preview **EUR 12,000.00**, submit, and return to Overview.

Click **Approve pending invoices**. Under the default 10,000 limit, show **PendingApproval / ManualReviewRequired** and the over-limit reason in the run result. The user's workspace retains that pending status. Running approval again must not turn it into an approval. If the configured limit differs, choose an allowed invoice amount above that limit; do not change production policy just for the presentation.

There is no human-review resolution screen in this demo. Stop at the persisted manual-review disposition; do not imply that anything has been paid.

### 6. Prove the permission wall

Before leaving the last run, show or record its evidence: the next action replaces the displayed run result. Click **Test permission boundaries**.

Expected: run **Succeeded**, with **two** execution rows:

- `D1-Invoice-Approval-Agent`: create attempted, outcome **Denied**, detail **Forbidden** (HTTP 403).
- `D1-Invoice-Entry-Agent`: approve attempted, outcome **Denied**, detail **Forbidden** (HTTP 403).

These are real API calls with the wrong agent permissions, not hidden buttons. The test also creates one legitimate Entry-owned pending invoice as its approval-denial target; that row can appear in the run table. Treat a missing row, **Unexpected**, or a failed run as a failed demonstration, not successful enforcement.

### 7. Prove user authorization separately

Use the second browser profile or **Sign out**, then open `<WebUrl>/sign-in` with the real account lacking `InvoiceSubmitter`. Entra may reuse an existing session, so confirm the intended account before continuing.

1. Open Overview. Expect the message "Your account is signed in but does not have InvoiceSubmitter access."
2. Open **New invoice** and click **Submit invoice** with valid fictional data. Expect the protected-API denial message, not a successful submission. The API returns 403 even though authentication succeeded.
3. Do not use `/dev/sign-in` or `?submitter=false` online; those routes are Development-only synthetic identities.
4. Sign out and restore the authorized presenter session.

### 8. Close with the four decisions

1. **Identity type:** Agent Identities for autonomous actors; real user sign-in for interactive work.
2. **Operating context:** autonomous application roles versus delegated user scope plus business authorization.
3. **Trust boundary:** one autonomous blueprint for the shared worker runtime and credential boundary.
4. **Attribution boundary:** separate Entry and Approval child identities, plus a separate interactive blueprint for its user context and lifecycle.

Final line: "One application can become a team. Give every accountable actor the identity boundary its responsibilities deserve."

## Suggested timing

Allow 10-12 minutes for the complete walkthrough. For seven minutes: orientation (0:00), autonomous invoices (0:45), interactive preview/submit (2:15), approve the user invoice (3:45), permission wall (4:45), unauthorized user (5:30), four decisions (6:20). Keep manual review as an optional extension or show a rehearsed result.

## Local rehearsal only

Start the four services with `docker compose up --build`. Open **http://localhost:8083/** for invoices and **http://localhost:8083/invoices/new** for the interactive assistant. Sign in at `http://localhost:8083/dev/sign-in`; use `http://localhost:8083/dev/sign-in?submitter=false` for the no-role user. Follow the same browser steps above, but expect mode **offline** and synthetic claims, never live Entra evidence.

For an automated local smoke run, use `pwsh -File ./Run-D1OfflineDemo.ps1`. It uses temporary ports 5080-5083 and stops its services on exit; its printed Web URL is not a persistent presentation site.

## Recovery and cleanup

- **Missing URL evidence:** obtain the Web Application URL from the deployed app's Azure portal Overview; if no deployment exists, use the setup guide. Localhost is not an online fallback.
- **Sign-in loop or redirect mismatch:** check the current HTTPS hostname and registered `/signin-oidc` redirect URI. See [troubleshooting](invoice-demo-troubleshooting.md).
- **401 / 403 or failed run:** check consent, audience, delegated scope, user business role, and the affected child agent's grants. Keep the correlation ID for diagnosis; never display tokens.
- **Invoices seem missing:** user submissions are in **Invoice workspace**; autonomous invoices are in the latest run table below it. Only ten user invoices are displayed. Reloading/navigating away clears the in-memory latest-run display, but does not delete persisted invoices or run evidence.
- **Live identity unavailable:** explicitly switch the presentation to local offline rehearsal or previously captured safe live evidence. Do not describe synthetic claims as live proof.
- **After the demo:** sign out, restore any disabled agent, restart the worker if token caches must be cleared, and verify Entry and Approval again. Repeated runs persist fictional records; data deletion and cloud cleanup require a separate approved action. Never switch an Azure host to offline mode.
