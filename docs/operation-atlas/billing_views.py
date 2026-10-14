"""Quote, credit, execution and physical-resource boundaries."""
def add_billing_views(add):
    root='Laplace/app/Laplace.Endpoints.OpenAICompat/'
    def view(id,title,body,files,note):
        add('monorepo-'+id,'Monorepo — '+title,body,[root+f for f in files],note,[])
    view('quote-preflight','quote creation and checkout handoff','''start
:Require catalog service and positive units;
:Calculate checked base fee + unit price times units;
:Create random quote ID; pending payment; expiry one hour;
:Persist quote before contacting Stripe;
:Read tenant entitlements; choose latest nonblank Stripe customer;
:Request checkout session with quote, price and customer;
if (Gateway reports checkout created?) then (yes)
 :Update quote with session ID, URL and pending status;
else (no)
 :Update quote to awaiting_manual_approval;
endif
:Return updated quote;
stop''',['Billing.cs','BillingPostgres/PostgresBillingQuoteStore.cs'],'Complete BillingOrchestrator preflight body reviewed. Quote persistence, provider call and final update are separate effects; exception/cancellation can leave the earlier pending quote. Stripe gateway internals, catalog price authority and provider acceptance remain separate. TryApproveQuoteAsync changes a found quote to approved; route authorization is outside that method.')
    view('quote-gate','billing admission and credit debit','''start
if (Billing bypass configured?) then (yes)
 :Allow with no quote;
 stop
endif
if (No quote ID?) then (yes)
 if (Tenant nonblank and known flat nonmetered service?) then (yes)
  :Attempt one-unit plan credit debit;
  if (Debit succeeded?) then (yes)
   :Persist zero-price approved_credit quote expiring in five minutes;
   :Allow with credit quote;
   stop
  endif
 endif
 :Deny quote_not_found;
 stop
endif
:Read quote; require exact tenant, case-insensitive service match,
unexpired quote and consumed=false;
if (Status approved or approved_credit?) then (yes)
 :Allow existing quote;
 stop
endif
:Attempt plan debit for all quoted units;
if (Debit succeeded?) then (yes)
 :Update quote approved_credit and price zero; allow;
elseif (Awaiting manual approval?) then (yes)
 :Deny payment_pending;
elseif (Stripe session exists?) then (yes)
 :Ask gateway whether session paid;
 if (Paid?) then (yes)
  :Update quote approved; allow;
 else (no)
  :Deny payment_pending;
 endif
else (no)
 :Deny payment_pending;
endif
stop''',['Billing.cs','QuoteGate.cs','BillingPostgres/PostgresBillingQuoteStore.cs'],'Gate checks and plan debit do not reserve a quote in the transaction that executes an operation. Debit can precede a failed quote write or later operation failure. Quote updates are unconditional upserts; concurrent admission of the same unconsumed approved quote is not excluded here. A billing gate is not a native resource grant, cognition completion or successful effect receipt.')
    view('quote-consume','quote consumption before inference and separate ledger','''start
:Chat/completion/embedding endpoint calls quote gate;
if (Gate denied?) then (yes)
 :Return payment-required response;
 stop
endif
if (Gate supplies quote?) then (yes)
 :Upsert supplied quote with consumed=true and status=consumed;
 :Insert billing usage record in separate store call;
endif
:Continue inference endpoint work;
note right
 Consumption and usage write precede inference.
 A later inference failure does not trigger a refund here.
 Failure after quote update can leave no usage record.
end note
stop''',['Billing.cs','QuoteGate.cs','EndpointMappings.Inference.cs','BillingPostgres/PostgresBillingQuoteStore.cs','BillingPostgres/PostgresBillingLedger.cs'],'Reviewed concrete endpoint ordering and both PostgreSQL stores. MarkConsumedAndRecordAsync does not compare-and-set consumed=false and does not share a transaction across quote and ledger updates. It records an ExecutedAt timestamp before later inference; the row alone is not proof that inference completed.')
    add('monorepo-credit-debit','Monorepo — plan-credit debit in each store','''start
:Reject nonpositive units at C# store boundary;
if (In-memory store?) then (yes)
 :Lock shared gate;
 :Select case-insensitive tenant, active status and current period;
 :Order by configured service credit limit descending;
 :Choose first with sufficient remaining credits;
 :Replace used-credit map; return debit; release lock;
else (PostgreSQL)
 :Call catalog billing.consume_credit;
 :SQL selects exact tenant, active status, period_end > now
and sufficient remaining service credits;
 :Order by configured credit limit descending; LIMIT 1 FOR UPDATE;
 :UPDATE used_credits and updated_at using selected row;
 :Return plan, remaining and period end, or no row;
endif
stop''',[root+'EntitlementBilling.cs',root+'BillingPostgres/PostgresBillingEntitlementStore.cs','Laplace/db/migrations/20260807020000_app_consume_credit.sql','Laplace/engine/core/src/sql_catalog.def'],'Both concrete debit bodies reviewed. PostgreSQL SQL does not check period_start <= now, unlike the in-memory branch. Both order by credit limit, not remaining headroom despite the SQL comment. SQL has no own positive-unit guard; the reviewed C# adapter supplies one. Debit is one database statement, separate from quote approval/consumption and operation execution. Installed SQL and concurrency acceptance were not tested.',[])
    add('monorepo-resource-plan','Monorepo — PostgreSQL resource ownership equation','''start
:Read physical RAM, performance cores, logical processors and maintenance width;
:Require RAM > 0; floor core/maintenance widths at one;
:p = performance cores; logical = max(p, logical processors);
:Ingest owners = control 1 + COPY p + fold p + slack 2 + observability 3;
:Serving owners = logical; maintenance owners = maintenance width;
:Reserve one operator connection; max connections = sum of owners;
:Parallel workers = p; I/O workers = logical;
:Worker processes = parallel + I/O; autovacuum workers = maintenance;
:Divide RAM into shared/backend/client quarters and OS remainder;
:Backend population = max connections + parallel + autovacuum workers;
:Per-backend share = max(1, backend bytes / population);
:Work memory = max(1, per-backend / 2); temp buffers = remainder;
:Maintenance/autovacuum work memory = per-backend share;
:Return immutable resource plan;
stop''',['Laplace/app/Laplace.Core/Core/PostgresResourcePlan.cs'],'Complete resource-equation source reviewed. It returns configuration values; it does not reserve memory or enforce every executor allocation. Topology detection and settings application remain nested. Effective cache size is shared plus OS cache, not an extra allocation. The equation alone does not prove a hard memory ceiling.',[])
