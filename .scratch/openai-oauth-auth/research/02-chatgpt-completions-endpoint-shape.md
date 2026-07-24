# 02 — What does a ChatGPT-session token actually authenticate against?

Status: answered
Source ticket: `.scratch/openai-oauth-auth/issues/02-chatgpt-completions-endpoint-shape.md`

## Method

Cloned the primary source directly: `git clone --depth 1 https://github.com/openai/codex.git`
(commit `c8957bbf0f79fa29c5e08b8c0b942c12ea3893f2`, fetched 2026-07-24). All claims below are
grep/read against that checkout unless marked otherwise. Paths are relative to `codex-rs/` in
that repo.

## Answer summary

A ChatGPT-session OAuth token authenticates against a **ChatGPT-account-scoped mirror of the
Responses API**, not the public OpenAI Platform API:

```
POST https://chatgpt.com/backend-api/codex/responses
```

(and, for the websocket transport, `wss://chatgpt.com/backend-api/codex/responses`). The wire
*shape* — request body fields, SSE event names, tool-call item shape — is the same Responses API
schema as `https://api.openai.com/v1/responses`. What differs is the **host/path** and the
**auth/session headers**. This endpoint is not `v1/chat/completions` and is not documented on
OpenAI's public API platform docs.

## Base URL / host / path

- `model-provider-info/src/lib.rs:38`:
  ```rust
  pub const CHATGPT_CODEX_BASE_URL: &str = "https://chatgpt.com/backend-api/codex";
  ```
- `model-provider-info/src/lib.rs` (`to_api_provider`, ~line 241-255): when the resolved
  `AuthMode` is `Chatgpt`, `ChatgptAuthTokens`, `Headers`, `AgentIdentity`, or
  `PersonalAccessToken`, the provider's default base URL is set to `CHATGPT_CODEX_BASE_URL`.
  For any other auth mode (plain API key) the default is `"https://api.openai.com/v1"`.
  ```rust
  let default_base_url = if matches!(auth_mode, Some(AuthMode::Chatgpt | AuthMode::ChatgptAuthTokens
      | AuthMode::Headers | AuthMode::AgentIdentity | AuthMode::PersonalAccessToken)) {
      CHATGPT_CODEX_BASE_URL
  } else {
      "https://api.openai.com/v1"
  };
  ```
- `codex-api/src/endpoint/responses.rs:100-102`: the Responses client's request path is the
  literal string `"responses"`.
- `codex-api/src/provider.rs` (`Provider::url_for_path`): concatenates `base_url` (trailing `/`
  trimmed) + `/` + `path` (leading `/` trimmed) — no other path segments are inserted.

  Net result for ChatGPT-session auth: `https://chatgpt.com/backend-api/codex` + `/responses`
  = `https://chatgpt.com/backend-api/codex/responses`.

- Confirmed independently: `core/src/config/mod.rs:4089` defaults `chatgpt_base_url` (a
  separate, related config field used for other ChatGPT backend-api calls, e.g. plugins/usage)
  to `"https://chatgpt.com/backend-api/"`. `CHATGPT_CODEX_BASE_URL` above already includes the
  `/codex` segment specific to the model/agent-turn endpoint.
- Also cross-checked against a live GitHub issue filed against openai/codex that names the exact
  URL in both transport forms: "Windows: responses_websocket connect to
  `chatgpt.com/backend-api/codex/responses` times out while responses_http succeeds" —
  https://github.com/openai/codex/issues/16367. This corroborates the source-code finding from
  an independent, externally observable angle (not used as the primary source, but consistent
  with it).

## Request shape: Responses API, not Chat Completions

`codex-api/src/common.rs:251-275` — the actual request struct sent to this endpoint:

```rust
pub struct ResponsesApiRequest {
    pub model: String,
    pub instructions: String,
    pub input: Vec<ResponseItem>,
    pub tools: Option<ResponsesApiTools>,
    pub tool_choice: String,
    pub parallel_tool_calls: bool,
    pub reasoning: Option<Reasoning>,
    pub store: bool,
    pub stream: bool,
    pub stream_options: Option<StreamOptions>,
    pub include: Vec<String>,
    pub service_tier: Option<String>,
    pub prompt_cache_key: Option<String>,
    pub text: Option<TextControls>,
    pub client_metadata: Option<HashMap<String, String>>,
}
```

This is the OpenAI **Responses API** wire shape (`model`, `instructions`, `input` as an item
array, `tools`/`tool_choice`, `reasoning`, `store`, `stream`, `include`, ...) — structurally the
same contract as the public `v1/responses` endpoint, *not* `v1/chat/completions`
(`messages: [...]`, `choices: [...]`). Codex CLI uses the Responses API shape for both API-key
auth and ChatGPT-session auth; the only default that changes with auth mode is the base URL
(see above). This is directly relevant to relay: `Clients/OpenAiClient.cs` (per the ticket
description; not present in this worktree checkout to re-read directly) sends a platform API key
to `v1/chat/completions`, an older/different wire contract than what a ChatGPT-session token
would need — a `chatgpt` backend in relay would need a Responses-API-shaped client, not a
Chat-Completions-shaped one, independent of the host/auth differences.

## Response shape: streaming SSE, standard Responses API events

- `codex-api/src/endpoint/responses.rs:148-151`: the HTTP transport explicitly sets
  `Accept: text/event-stream` on the request — the endpoint is consumed as a **streaming SSE**
  response, not a single JSON blob.
- `codex-api/src/sse/responses.rs` parses named SSE events matching the public Responses API
  streaming event taxonomy verbatim, e.g. (lines ~331-460): `response.output_item.done`,
  `response.output_item.added`, `response.output_text.delta`,
  `response.custom_tool_call_input.delta`, `response.reasoning_summary_text.delta`,
  `response.reasoning_summary_text.done`, `response.reasoning_text.delta`, `response.created`,
  `response.failed`, `response.incomplete`, `response.completed`,
  `response.reasoning_summary_part.added`.
- `codex-api/src/common.rs:76-123` (`ResponseEvent`) is Codex's internal decoded form of those
  SSE events, including `ToolCallInputDelta { item_id, call_id, delta }`, `OutputItemDone`,
  `OutputItemAdded`, `Completed { response_id, token_usage, end_turn }`.
- Tool calls are represented as typed items inside the `input`/output item stream
  (`protocol/src/models.rs:799+`, `enum ResponseItem`), matching Responses API conventions:
  - `FunctionCall { id, name, arguments: String, call_id, namespace, ... }` — `arguments` is a
    JSON-encoded *string*, not a parsed object (the comment in source notes this explicitly and
    that the caller must `serde_json` it separately).
  - `FunctionCallOutput { call_id, output, ... }` for the result.
  - Also present: `LocalShellCall`, `ToolSearchCall`, `CustomToolCall` /
    `CustomToolCallOutput` — provider-specific tool item variants layered on the same Responses
    item model.
- There is also a **WebSocket transport** for the identical logical endpoint/path
  (`codex-api/src/endpoint/responses_websocket.rs`, connecting via
  `provider.websocket_url_for_path("responses")`, which upgrades `https://` to `wss://` on the
  same base URL/path) — i.e. `wss://chatgpt.com/backend-api/codex/responses`. This is an
  alternate transport for the same request/response contract, not a different API surface.

## Headers required beyond `Authorization: Bearer <token>`

Primary source: `model-provider/src/bearer_auth_provider.rs` (`BearerAuthProvider::add_auth_headers`)
and `model-provider/src/auth.rs` (`AgentIdentityAuthProvider::add_auth_headers`), both exercised
by unit tests in `model-provider/src/auth.rs` (e.g. `bearer_auth_provider_adds_auth_headers`,
`agent_identity_auth_provider_preserves_account_routing_headers`):

```rust
impl AuthProvider for BearerAuthProvider {
    fn add_auth_headers(&self, headers: &mut HeaderMap) {
        if let Some(token) = self.token.as_ref() { .. } // Authorization: Bearer <token>
        if let Some(account_id) = self.account_id.as_ref() { .. } // ChatGPT-Account-ID: <id>
        if self.is_fedramp_account { .. } // X-OpenAI-Fedramp: true
    }
}
```

Confirmed headers attached to ChatGPT-session-authenticated requests:

- `Authorization: Bearer <access_token>` — required.
- `ChatGPT-Account-ID: <account_id>` — the workspace/account id extracted from the ID token
  (`chatgpt_account_id` claim; see `login/src/auth/manager.rs:776`,
  `login/src/token_data.rs`). Sent whenever an account id is known. Test evidence:
  `model-provider/src/auth.rs` test `chatgpt_bootstrap_unavailable_uses_session_bearer_fallback`
  asserts `ChatGPT-Account-ID: account-123` alongside `Authorization: Bearer test-access-token`.
- `X-OpenAI-Fedramp: true` — conditionally sent for FedRAMP-flagged accounts only.
- `originator: codex_cli_rs` (or an overridden value; see `DEFAULT_ORIGINATOR` in
  `login/src/auth/default_client.rs:40`) and a `User-Agent` string — applied to *all* Codex CLI
  HTTP requests by the default HTTP client (`login/src/auth/default_client.rs`), not specific to
  this endpoint, but present on every call including this one. This looks like it may function as
  a client-allowlist signal on the backend side, though that isn't provable from client source
  alone.
- Optional, non-auth headers added by the Responses endpoint itself when available
  (`codex-api/src/requests/headers.rs`, `codex-api/src/endpoint/responses.rs:87-94`):
  `x-client-request-id` (thread id), `session-id`, `thread-id`, `x-openai-subagent`.
- For the separate first-party "agent identity" auth mode (JWT-signed assertions rather than a
  bearer token; used opportunistically when available —
  `model-provider/src/auth.rs::AgentIdentityAuthProvider`), the `Authorization` header is
  `AgentAssertion <jwt>` instead of `Bearer <token>`, with the same `ChatGPT-Account-ID` and
  `X-OpenAI-Fedramp` headers attached. This is a distinct, more specialized auth path Codex
  falls back away from if unavailable (`resolve_provider_auth_for_scope`,
  `model-provider/src/auth.rs:199-257`) — the plain ChatGPT-session bearer token above is the
  baseline/fallback path and the one directly relevant to "Sign in with ChatGPT" as described in
  ticket 01.

## Documented vs. reverse-engineered, and stability implications

- `https://chatgpt.com/backend-api/codex/responses` does **not** appear anywhere in OpenAI's
  public API Platform docs (`https://platform.openai.com` / `https://developers.openai.com`).
  A web search for the exact path turned up only: the openai/codex GitHub repo itself, GitHub
  issues filed against it by users, and third-party community projects that reverse-engineer or
  proxy this same endpoint (e.g. `openai-oauth`, `codex-lb`, `openai-api-server-via-codex`) —
  not first-party documentation. Search performed 2026-07-24; see links below.
- This means: the endpoint's *existence and shape* are known with high confidence because the
  Codex CLI source is public and OpenAI-maintained (a genuine primary source, not a guess), but
  it is **not a stable, versioned, publicly-documented API contract**. It is the internal backend
  the ChatGPT product's own agent surface uses. OpenAI has made no compatibility promises about
  it; it can change shape, add required headers, or be revoked/rate-limited independently of the
  documented `api.openai.com` platform API. A `chatgpt` backend in relay built against this
  endpoint should be treated as coupled to Codex CLI's own compatibility, not to OpenAI's
  published API stability guarantees — e.g., pinning behavior to what a recent Codex CLI release
  does, and expecting to need updates when Codex CLI's own request/header shape changes.
- Practical implication flagged by the source itself: `codex-api/src/provider.rs` treats
  `chatgpt.com`-style base URLs as effectively provider-detected (see `is_azure_responses_provider`
  and adjacent base-URL-sniffing logic elsewhere in the crate) rather than a documented,
  swappable "provider" the way `api.openai.com` is — reinforcing that this is a special-cased,
  not general-purpose, integration point in Codex's own code.

## Open questions / not fully confirmed

- Whether `ChatGPT-Account-ID` is strictly *required* by the backend (i.e. requests fail without
  it) versus merely sent-when-known could not be confirmed from client source alone — the client
  always sends it when the value is available, but server-side enforcement isn't visible from
  this repo.
- Rate limits, quota semantics, and error-response shape for this endpoint were out of scope for
  this ticket and not investigated here.

## Sources

- Primary: https://github.com/openai/codex (cloned locally, commit `c8957bbf0f79fa29c5e08b8c0b942c12ea3893f2`), files cited inline above:
  - `codex-rs/model-provider-info/src/lib.rs`
  - `codex-rs/codex-api/src/provider.rs`
  - `codex-rs/codex-api/src/endpoint/responses.rs`
  - `codex-rs/codex-api/src/endpoint/responses_websocket.rs`
  - `codex-rs/codex-api/src/common.rs`
  - `codex-rs/codex-api/src/sse/responses.rs`
  - `codex-rs/protocol/src/models.rs`
  - `codex-rs/model-provider/src/auth.rs`
  - `codex-rs/model-provider/src/bearer_auth_provider.rs`
  - `codex-rs/login/src/auth/manager.rs`
  - `codex-rs/login/src/auth/default_client.rs`
  - `codex-rs/core/src/config/mod.rs`
- Corroborating (non-primary, used only to sanity-check host/path and documentation status):
  - [Windows: responses_websocket connect to chatgpt.com/backend-api/codex/responses times out while responses_http succeeds · Issue #16367 · openai/codex](https://github.com/openai/codex/issues/16367)
  - [Constant requests to `https://chatgpt.com/backend-api/wham/usage` even in API mode · Issue #10869 · openai/codex](https://github.com/openai/codex/issues/10869)
  - [OpenAI API Platform Documentation](https://developers.openai.com/api/docs) (checked — does not document this host/path)
