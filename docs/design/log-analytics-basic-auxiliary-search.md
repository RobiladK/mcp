<!-- cspell:ignore externaldata SRCH unparseable -->

# Log Analytics Basic and Auxiliary Search

This document describes the design of `monitor workspace log search` (`monitor_workspace_log_search`), the tool that searches Basic and Auxiliary Log Analytics tables.

## Why a separate tool

Azure Monitor exposes two synchronous log paths:

- `/v1/workspaces/{workspaceId}/query` serves Analytics-plan tables and backs the existing `monitor workspace log query` and `monitor resource log query` tools.
- `/v1/workspaces/{workspaceId}/search` serves Basic and Auxiliary tables and has different source, operator, time-range, concurrency, cost, and partial-result semantics.

The two paths are not interchangeable, so search is a sibling tool rather than a behavior change to the existing query tools. Auto-routing `monitor workspace log query` by table plan was rejected: it would require a metadata call on every query, make cost and tool selection unpredictable, and cannot safely infer the primary source of arbitrary caller KQL.

## Tool boundary

| Aspect | Decision |
| --- | --- |
| Scope | One workspace, one primary Basic or Auxiliary table |
| Endpoint | Workspace synchronous `/search` |
| Resource-centric search | Not supported; Azure documents `/search` as workspace-only |
| Analytics tables | Continue to use `monitor workspace log query` |
| Search jobs | Out of scope (see below) |

The command is read-only, idempotent, non-destructive, and transport-neutral, so it behaves identically in stdio and HTTP modes.

## Server discovery modes

| Server start configuration | Exposed tool | Routed command |
| --- | --- | --- |
| `--mode all --namespace monitor` | `monitor_workspace_log_search` | Direct tool call |
| `--tool monitor_workspace_log_search` | `monitor_workspace_log_search` | Direct tool call |
| Default mode or `--mode namespace --namespace monitor` | `monitor` | `monitor_workspace_log_search` |
| `--mode single --namespace monitor` | `azure` | Tool `monitor`, command `monitor_workspace_log_search` |
| `--mode consolidated --namespace monitor` | `get_azure_resource_and_app_health_status` | `get_azure_resource_and_app_health_status_monitor_workspace_log_search` |

Direct mode advertises the command's `WorkspaceLogSearchResult` output schema when structured output is enabled. Namespace and consolidated modes wrap the result in the repository's aggregate `tool-result` envelope. Single mode wraps the downstream MCP call result and forwards the structured-output setting to its child Monitor server.

## Input contract

| Option | Required | Meaning |
| --- | --- | --- |
| `subscription` | Yes | Subscription ID or name, resolved through the standard subscription resolver. |
| `resourceGroup` | Yes | Required because workspace names are not unique within a subscription. |
| `workspace` | Yes | Workspace name. The server resolves the data-plane customer GUID; callers never supply it. |
| `table` | Yes | One ASCII KQL identifier that must exist with plan `Basic` or `Auxiliary`. |
| `query` | Yes | A KQL pipeline fragment whose first non-whitespace character is `\|`. It never names the primary table. |
| `timespan` | Yes | A positive ISO 8601 duration or a closed RFC 3339 `start/end` interval, at most 30 days. |
| `limit` | No | 1-100, default 20. |
| `tenant` | No | Tenant ID or name. |

## Server-bound table

The server composes the final query as `<table> <pipeline> | take <limit>`. Callers cannot supply a complete KQL statement, because that would let them substitute a different primary source and bypass the plan and cost guards.

A dedicated `LogSearchQueryValidator` enforces this boundary instead of the shared `KqlQueryValidator`; search has narrower source and operator rules, and weakening the shared Analytics validator would affect unrelated commands. The validator is a conservative structural check, not a query planner: it rejects source functions (`table()`, `workspace()`, `app()`, `resource()`, `cluster()`), nested tabular pipelines, multiple statements, and Azure's unsupported operators (`join`, `find`, `search`, `externaldata`, `invoke`). Azure remains authoritative for `union`, `lookup`, and the rest of KQL syntax and semantics.

The final `take` is always appended, even when the caller's pipeline already ends in `take` or `limit`.

## Table plan and transitions

Before any data-plane call, the tool reads the exact table's `Plan` and `LastPlanModifiedDate` from the `Azure.ResourceManager.OperationalInsights` SDK. Validation fails closed:

- Missing table returns 404.
- An Analytics or otherwise unsupported plan returns 409 and points to `monitor workspace log query`.
- Missing or unparseable plan metadata returns 502 with no data-plane request.
- An interval crossing `lastPlanModifiedDate` returns 409 and instructs the caller to query the supported portion beginning at that boundary. Without this, one response could span different access behaviors and still look complete.

A plan change between metadata read and the request is an unavoidable race; the service error is surfaced and the tool never falls back to `/query`.

## Result contract and partial results

Results are typed JSON — `columns`, positional `rows`, `rowCount`, `limit`, `isPartial`, and `error` — rather than TSV or dictionaries of stringified values, so schema and null/numeric/boolean types survive.

Azure documents `PartialError` as non-fatal and can return usable tables alongside it. The tool returns those rows with `isPartial: true` and structured error details, letting the agent decide whether incomplete data is useful. It never returns rows with `isPartial: false` when the response was incomplete. A fatal embedded error, malformed response, row-width mismatch, or payload above the 1 MiB ceiling returns an error and no success-shaped result; nothing is silently truncated. HTTP 204 maps to zero rows with `isPartial: false`.

## Authentication and cloud

Credentials come from the existing tenant/subscription resolution and `IAzureTokenCredentialProvider`; tokens and workspace GUIDs are never public options. The tool needs workspace read, ARM table read, and workspace query permissions only — notably not search-job write permissions.

Endpoint and token scope resolution is centralized in one Monitor-internal resolver keyed by cloud type. Public cloud maps to the endpoint currently documented for Basic/Auxiliary `/search`. A cloud without a verified endpoint and scope mapping fails closed rather than sending credentials to a guessed host, and there is no automatic endpoint fallback: a second POST would duplicate a billable scan and make failure semantics ambiguous. The general Log Analytics API is migrating hosts, so isolating the pair keeps that a resolver-only change.

## Limits and cost

Scan cost is driven by the volume ingested into the table across `timespan`, not by the number of rows returned. `limit` bounds agent context, not billing, and both the tool description and the CLI reference say so.

Version one requires an explicit timespan and caps a single invocation at 30 days for both plans. Azure imposes that limit on Basic; Auxiliary can address its retained range, but a common ceiling bounds broad-scan cost and latency without adding a bypass flag. Callers can issue deliberate, non-overlapping windows. Raising the Auxiliary ceiling later needs usage and cost evidence and is not an API contract change.

There is no application retry of the billable POST, and no caching, pagination, or internal fan-out — the latter would work against Azure's two-concurrent-query limit for Basic/Auxiliary. Throttling is surfaced as 429 with `Retry-After` so the caller controls any later attempt.

## Why search jobs are separate

Search jobs are asynchronous ARM table-creation operations that write a persistent `*_SRCH` Analytics table. They can run for up to 24 hours, require table and search-job write permissions, incur both scan and result-ingestion costs, and need status and cleanup operations. Folding that lifecycle into a synchronous read-only tool would broaden permissions, side effects, command metadata, cost, and failure handling. If demand appears, search jobs belong in separate create/status/delete lifecycle tools.

## References

- [Query data in a Basic and Auxiliary table](https://learn.microsoft.com/azure/azure-monitor/logs/basic-logs-query)
- [Access the Azure Monitor Log Analytics API](https://learn.microsoft.com/azure/azure-monitor/logs/api/access-api)
- [Log Analytics API response format](https://learn.microsoft.com/azure/azure-monitor/logs/api/response-format)
- [Azure Monitor service limits: log queries and language](https://learn.microsoft.com/azure/azure-monitor/fundamentals/service-limits#log-queries-and-language)
- [Configure a table plan](https://learn.microsoft.com/azure/azure-monitor/logs/logs-table-plans)
- [Run search jobs in Azure Monitor](https://learn.microsoft.com/azure/azure-monitor/logs/search-jobs)
