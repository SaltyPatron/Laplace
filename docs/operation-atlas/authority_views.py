"""Monorepo identity, platform authority and billing ownership, as implemented."""
def add_authority_views(add):
    root='Laplace/app/Laplace.Endpoints.OpenAICompat/'
    def view(id,title,body,files,boundary):
        add('monorepo-authority-'+id,'Monorepo — '+title,body,[root+f for f in files],boundary,[])
    view('routing','request routing before local authorization','''start
:Apply forwarded headers from configured loopback proxies;
:When request is HTTPS and public origin configured, rewrite scheme/host;
if (Exact /refactor path?) then (yes)
 :308 redirect to slash form;
 stop
elseif (/refactor/ prefix?) then (yes)
 :Run fixed-origin proxy; upstream owns authentication;
 stop
endif
:Response compression; default files and static files;
if (Static file handled?) then (yes)
 stop
endif
:Rate limiter; correlation ID; exception envelope;
:Browser authentication;
:API-key / platform / workspace enforcement;
:Billing account boundary;
:Code-model chat middleware;
:Matched local endpoint or fallback;
stop''',['Program.cs','RefactorProxyMiddleware.cs'],'Ordering is taken from Program.cs. Proxy requests finish before the local rate limiter, exception envelope and authentication middleware. This does not establish the upstream authorization configuration. Endpoint routing/framework internals, browser login provider and code-model interception remain separately inspectable boundaries.')
    view('tenant','resolve request tenant and workspace','''start
if (Tenant cached in HttpContext.Items?) then (yes)
 :Return cached resolution;
 stop
endif
:Recognize sk-laplace- Bearer key; otherwise X-Api-Key with that prefix;
if (Recognized key presented?) then (yes)
 :Validate key through key service;
 :Return key tenant or invalid_key;
else (no)
 if (Authorization or X-Api-Key header exists?) then (yes)
  :Return invalid_key; do not fall through to browser/dev;
 elseif (Authenticated browser has tenant claim and GUID user claim?) then (yes)
  if (Workspace cookie is nonempty and at most 160 characters?) then (yes)
   :Query current membership of selected workspace;
   if (Membership exists?) then (yes)
    :Clone request principal; replace tenant claims;
    :Store selected workspace role in request items;
   else (no)
    :Delete selection cookie; retain original ticket tenant;
   endif
  endif
  :Return browser tenant with user/provider claims;
 elseif (Dev principal configured?) then (yes)
  :Return configured dev tenant;
 else (no)
  :Return trimmed X-Laplace-Tenant or local-dev as header tenant;
 endif
endif
:Cache resolution for this request;
stop''',['Auth/TenantResolution.cs','AppComposition.cs'],'Tenant resolution is not authorization. ApiKeyTenantResolver is registered in all modes; non-header modes enforce identity in separate middleware. Workspace selection is checked against membership and mutates only the request principal, not the immutable login ticket. SQL membership function and browser ticket validation remain nested.')
    view('enforcement','route, browser membership and platform enforcement','''start
if (Outside /v1 and matched chess operations?) then (yes)
 :Continue;
 stop
elseif (POST Stripe webhook?) then (yes)
 :Continue to downstream webhook boundary;
 stop
elseif (Admin / billing operator / catalog-sync route?) then (yes)
 :Set private no-store;
 :For browser-user mutation require SameOrigin;
 :Require route-specific platform permission;
 :Set platform_authorized request flag; continue;
 stop
endif
:Resolve tenant; reject invalid_key with 401;
if (Identity mode requires identity and request lacks browser/key/dev identity?) then (yes)
 :Allow only enumerated public GET/HEAD reads,
or exact /v1/op with operator credential; otherwise 401;
endif
if (Browser tenant?) then (yes)
 :Require SameOrigin for methods other than GET/HEAD/OPTIONS;
 if (Outside /v1/auth?) then (yes)
  :Read selected role or query current membership;
  :Missing membership rejects with 403 except access/account-discovery routes;
  :Store request workspace role;
  :For key/checkout/portal management and plan POST require owner/admin;
 endif
endif
:Continue request;
stop''',['Auth/TenantResolution.cs','Auth/PlatformAccess.cs','Program.cs'],'Complete middleware reviewed. SameOrigin compares supplied Origin scheme/authority against current request; without Origin it accepts Sec-Fetch-Site=same-origin or X-Laplace-Request=1. Public routes still reject a supplied invalid key. Webhook bypass here is not webhook-signature verification. Header mode permits unresolved header tenants; deployment mode is configuration, not inferred from source.')
    view('platform','platform permission and serialized role changes','''start
if (Checking platform permission?) then (yes)
 if (Dev principal configured or matching operator token?) then (yes)
  :Allow platform action;
 elseif (Authorization or API-key header supplied?) then (yes)
  :Deny platform authorization through browser fallback;
 else (browser)
  :Read authenticated user platform role;
  :Check observer/operator/administrator permission set;
 endif
 stop
endif
:SetRole validates requested role; opens DB transaction;
:Lock app.platform_roles SHARE ROW EXCLUSIVE;
if (Actor user supplied?) then (yes)
 :Recheck actor remains administrator within transaction;
endif
:Require target user exists; read previous role;
if (Role unchanged?) then (yes)
 :Commit without role event;
 stop
endif
if (Removing administrator?) then (yes)
 :Require more than one current administrator;
endif
:Delete role or upsert new role;
:Insert event with previous/new role and actor or installation-credential;
:Commit role and audit event together;
stop''',['Auth/PlatformAccess.cs','Auth/TenantResolution.cs'],'Complete platform store and policy reviewed. DevPrincipalActive makes HasCredential return true before token comparison, irrespective of supplied credentials; tenant resolution has a narrower no-credential dev fallback. Operator token comparison is fixed-time after equal-length check. Platform roles and workspace membership are separate tables/decisions; this is not semantic evidence standing.')
    view('keys','API-key issue, validation, touch and revocation','''start
if (Issue?) then (yes)
 :Generate 24 random bytes; lowercase hex with sk-laplace- prefix;
 :SHA256 full key; store hash, 16-character display prefix, tenant and label;
 :Await store write; return plaintext key and record;
elseif (Validate?) then (yes)
 :Require prefix; hash full presented key; query store;
 if (Missing or revoked?) then (yes)
  :Return no identity;
 else (active)
  :Write LastUsedAt; return previously read record;
 endif
elseif (Revoke by prefix?) then (yes)
 :Read tenant keys; choose first active exact-prefix match;
 :Revoke selected key hash or return false;
else (list)
 :Read keys by tenant or exact label;
endif
stop''',['Auth/ApiKeys.cs'],'Complete key service and both stores reviewed. Validation performs a read then separate usage write; revocation is not transactionally rechecked before returning the previously read record. In-memory touch/revoke use dictionary read/replace operations and do not provide an atomic compare-update across both. PostgreSQL issue uses ON CONFLICT DO NOTHING; tenant foreign-key violation has a specific error. Plaintext is returned by issuance, not stored by these stores.')
    view('billing','workspace ownership before checkout and subscription','''start
:For /v1 or /chess resolve tenant;
:Private no-store for authenticated tenants and auth/account routes;
if (Browser expected-workspace header differs outside auth/admin/operator?) then (yes)
 :409 workspace_changed;
 stop
endif
if (Not authenticated browser/key/dev billing route, or webhook/operator?) then (yes)
 :Continue;
 stop
endif
:For quote GET require quote tenant equals resolved tenant; otherwise 404;
if (JSON POST?) then (yes)
 :Limit/buffer body to 1 MiB; require object;
 :Reject duplicate case-insensitive identity fields when read;
 :Supplied nonblank tenant must equal resolved tenant;
 :Key redemption requires checkout session owned by tenant;
 :Preflight for subscription service requires integer units = 1;
 :Rewind request body even on parsing failure;
endif
if (Subscription purchase?) then (yes)
 :Browser requires workspace owner/admin;
 :Require Stripe key and, in identity mode, PostgreSQL billing storage;
 :Live Stripe keys require commercial catalog approval;
 :Read existing tenant entitlements;
 if (Existing subscription has active/trialing/past_due/unpaid/incomplete/paused status?) then (yes)
  :409 subscription_already_exists;
  stop
 endif
endif
:Continue to billing endpoint;
stop''',['Auth/BillingAccountBoundaryMiddleware.cs','Auth/TenantResolution.cs','AppComposition.cs'],'Complete ownership middleware reviewed. It validates request ownership and selected subscription prerequisites; it does not itself charge, grant entitlement or reserve a unique subscription transaction. Duplicate-property detection applies only to queried identity fields. Existing-subscription read and downstream purchase are separate operations. Billing enforcement bypass is a different option whose default is true in AppComposition.')
    view('proxy','fixed-origin Refactor streaming proxy','''start
:Reject CONNECT with 405;
:Require literal /refactor/ raw target; reject control, backslash and fragment;
:Strip prefix; concatenate fixed http://127.0.0.1:55434;
:Preserve escaped path/query; assert fixed authority;
:Stream request body; forward end-to-end headers;
:Remove Host and hop-by-hop/Connection-nominated headers;
:Send without redirects, cookies, proxy or decompression;
:Ten-second connection timeout; no overall client timeout;
if (Upstream switches protocol?) then (yes)
 :502; stop;
 stop
endif
:Forward status and end-to-end response headers;
:Disable buffering; SSE sets X-Accel-Buffering=no;
:Start response; HEAD/204/304 return without body;
:Otherwise read/write/flush each chunk until end;
note right
 Caller cancellation aborts client response.
 Transport failure before response start gives 502;
 after response start aborts connection.
end note
stop''',['RefactorProxyMiddleware.cs','Program.cs'],'Entire proxy reviewed. It adds no credential, but forwards incoming end-to-end Authorization/Cookie headers unless excluded by Connection. Upstream authentication is a separate boundary. It does not support CONNECT or protocol upgrade. Client-owned cancellation controls streams; pooled buffers are returned in finally.')
