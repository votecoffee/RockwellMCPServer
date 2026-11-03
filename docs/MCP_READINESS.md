# Existing-service readiness

The immutable MCP frontdoor policy can select `existing-genuine-only`. In this
mode the frontend only observes an already-running genuine service. It cannot
start, restart, ensure or recover a service, modify its startup journal, or
change engineering admission and controller policy.

Before the connection has verified an owner, a discovery failure returns
`mcp_existing_service_unavailable` with `phase: "unbound-discovery"`,
`dispatched: false` and `automaticRetry: false`. If that observation task actually
faulted, a **later explicit request** may make one fresh observation. Each request
can observe at most once; the failed request and its engineering body are never
replayed. Concurrent callers share an observation while it is running.

Canceling a caller only cancels its wait. It does not cancel, clear or replace
the shared observation. An observation that itself is canceled is not a faulted
task and is not replaced. A successful observation pins the exact process
identity even if every waiting caller canceled before it finished.

Once an owner has been verified, the connection permanently retains that owner.
If its identity is absent, different or unverifiable, requests return
`phase: "bound-owner-unavailable"` and `laterExplicitRequestMayObserve: false`.
That connection remains unavailable even if a later identity query succeeds;
it never reobserves or adopts a replacement. Recover the service separately and
open a new connection through the client's supported refresh or reconnect flow.
Readiness never retries an engineering request after dispatch.

The error's `laterExplicitRequestMayObserve` field describes whether an unbound
connection can make another observation. It grants no service-start authority
and does not report whether a service has subsequently become available.

This frontend change requires a reviewed immutable frontend package and a new
client connection. It does not hotpatch an already-running frontend. Extension
catalog reload is a separate workflow; see [extensions](EXTENSIONS.md).
