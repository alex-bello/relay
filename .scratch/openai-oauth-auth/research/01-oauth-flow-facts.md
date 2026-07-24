# 01 — OAuth flow facts for "Sign in with ChatGPT"

Status: findings for ticket `.scratch/openai-oauth-auth/issues/01-oauth-flow-facts.md`

## Primary source

All facts below are read directly out of OpenAI's own Codex CLI source, repo `openai/codex`
(https://github.com/openai/codex), specifically the `codex-rs/login` crate that implements
`codex login` / `~/.codex/auth.json`. Every fact is cited to a file path in that repo. Snapshot
used: `main` branch, commit `c8957bbf0f79fa29c5e08b8c0b942c12ea3893f2` (2026-07-24). Line numbers
may drift on newer commits; the file paths and identifiers (constant names, function names) are
the stable citation anchors.

No blog posts, third-party write-ups, or non-OpenAI sources were used for any claim below. Where a
detail could not be confirmed from this source, it is marked explicitly as unconfirmed rather than
guessed.

---

## 1. Authorization and token endpoints

- **Issuer / host:** `https://auth.openai.com`
  Source: `codex-rs/login/src/server.rs`, `pub(super) const DEFAULT_ISSUER: &str = "https://auth.openai.com";`

- **Authorization endpoint:** `GET https://auth.openai.com/oauth/authorize`
  Built by `build_authorize_url()` as `format!("{issuer}/oauth/authorize?{qs}")`.
  Source: `codex-rs/login/src/server.rs`, function `build_authorize_url`.

- **Token endpoint (code exchange and refresh):** `POST https://auth.openai.com/oauth/token`
  - Code exchange: `exchange_code_for_tokens()` builds `format!("{}/oauth/token", issuer...)`.
    Source: `codex-rs/login/src/server.rs`, function `exchange_code_for_tokens`.
  - Refresh: `const REFRESH_TOKEN_URL: &str = "https://auth.openai.com/oauth/token";` — same
    endpoint as code exchange, distinguished only by `grant_type`.
    Source: `codex-rs/login/src/auth/manager.rs`, line ~191.
  - Also used for a third grant type — RFC 8693 token-exchange — to mint a platform-style API
    key from the ChatGPT id_token (see §7 below).

- **Revoke endpoint (used on logout):** `POST https://auth.openai.com/oauth/revoke`
  Source: `codex-rs/login/src/auth/manager.rs`, `pub(super) const REVOKE_TOKEN_URL`, consumed in
  `codex-rs/login/src/auth/revoke.rs`.

- Both the refresh endpoint and the client id are overridable via env vars
  (`CODEX_REFRESH_TOKEN_URL_OVERRIDE`, `CODEX_REVOKE_TOKEN_URL_OVERRIDE`,
  `CODEX_APP_SERVER_LOGIN_CLIENT_ID`) — internal/test escape hatches, not part of the normal flow.
  Source: `codex-rs/login/src/auth/manager.rs`.

## 2. Client ID

Fixed, published, public PKCE client id:

```
app_EMoamEEZ73f0CkXaXp7hrann
```

Source: `codex-rs/login/src/auth/manager.rs`, `pub const CLIENT_ID: &str = "app_EMoamEEZ73f0CkXaXp7hrann";`
Also embedded literally in the TUI onboarding auth URL builder:
`codex-rs/tui/src/onboarding/auth.rs` (`"client_id=app_EMoamEEZ73f0CkXaXp7hrann&"`).

There is no client secret anywhere in this crate — consistent with a public (PKCE) OAuth client
that cannot keep a secret. `oauth_client_id()` resolves to this constant unless overridden by the
internal env var above.

## 3. PKCE parameters

- **Code verifier:** 64 random bytes, base64url-encoded without padding (`URL_SAFE_NO_PAD`).
- **Code challenge:** `BASE64URL(SHA256(code_verifier))`, no padding.
- **Code challenge method:** `S256` (sent explicitly as `code_challenge_method=S256` in the
  authorize URL).
  Source: `codex-rs/login/src/pkce.rs` (function `generate_pkce`) and
  `codex-rs/login/src/server.rs` (`build_authorize_url`).

- **Scopes requested** (space-delimited `scope` param):
  ```
  openid profile email offline_access api.connectors.read api.connectors.invoke
  ```
  Source: `codex-rs/login/src/server.rs`, `build_authorize_url`.

- **Other non-standard authorize params Codex sends:**
  - `id_token_add_organizations=true`
  - `codex_cli_simplified_flow=true`
  - `originator=<client name>` (from `codex-rs/login/src/auth/default_client.rs::originator()`)
  - `state=<32 random bytes, base64url>` — CSRF protection, checked on callback
    (`generate_state()`, same file).
  - optional `allowed_workspace_id=<comma-joined ids>` when workspace-restricted login is forced.
  Source: `codex-rs/login/src/server.rs`, `build_authorize_url`.

## 4. Redirect URI / loopback convention

- Redirect URI sent to the authorize endpoint: `http://localhost:{port}/auth/callback`
  (note: `localhost`, not `127.0.0.1`, even though the listener itself binds to `127.0.0.1`).
  Source: `codex-rs/login/src/server.rs`, `run_login_server`
  (`let redirect_uri = format!("http://localhost:{actual_port}/auth/callback");`).

- **Port is a small fixed set, not a dynamically/OS-chosen ephemeral port:**
  - Primary: `1455` (`const DEFAULT_PORT: u16 = 1455;`)
  - Fallback: `1457` (`const FALLBACK_PORT: u16 = 1457;`), used only if the primary port cannot be
    bound after retries.
  - The source comment is explicit about why it's not dynamic: `// Keep in sync with the Codex CLI
    Hydra redirect URI allow-list.` — i.e. the OAuth app's registered redirect URIs on the
    authorization server are an allow-list of specific `localhost:<port>` values, so Codex cannot
    just bind port 0 and use whatever the OS hands back.
  Source: `codex-rs/login/src/server.rs`, top-of-file constants and `bind_server()`.

- **Binding/retry algorithm** (`bind_server`, same file):
  1. Try `127.0.0.1:1455`.
  2. If `AddrInUse`: on the first such failure, send an HTTP `GET /cancel` to whatever is
     listening on port 1455 — on the assumption it's a stale login server from a previous
     `codex login` attempt — to try to free the port (`send_cancel_request`).
  3. Retry binding every 200ms, up to 10 attempts.
  4. If still failing after 10 attempts and the port in question was the default (1455), switch to
     the fallback port 1457 and repeat the retry loop once.
  5. If binding still fails (i.e. both 1455 and 1457 are unavailable), return an `AddrInUse`
     `io::Error` up to the caller — login fails with a clear "port already in use" error rather
     than hanging silently.

## 5. Token response shape

Code-exchange request (`exchange_code_for_tokens`) is `application/x-www-form-urlencoded`:
```
grant_type=authorization_code&code=...&redirect_uri=...&client_id=...&code_verifier=...
```
Response body is JSON, deserialized as:
```rust
struct TokenResponse {
    id_token: String,
    access_token: String,
    refresh_token: String,
}
```
Source: `codex-rs/login/src/server.rs`, `exchange_code_for_tokens`.

- All three fields (`id_token`, `access_token`, `refresh_token`) are **required** (non-`Option`) in
  the initial code-exchange response — i.e. a refresh token is always issued on first login,
  consistent with the `offline_access` scope requested.
- Both `id_token` and `access_token` are JWTs. The comment on `TokenData::access_token` states
  explicitly: `/// This is a JWT.` (`codex-rs/login/src/token_data.rs`).
- **No `expires_in` field is present or read anywhere in this crate for the ChatGPT OAuth
  token response.** Codex does not track a separate expiry value from the token response; instead
  it decodes the access token JWT itself and reads its own `exp` claim
  (`codex-rs/login/src/token_data.rs::parse_jwt_expiration`, used from
  `codex-rs/login/src/auth/manager.rs::should_refresh_proactively`). **The actual numeric token
  lifetime (e.g. "1 hour") is therefore not stated anywhere in the Codex source — it's whatever
  `exp` the auth server puts in the JWT at issuance time, and Codex source does not hardcode or
  assert a value.** This is a confirmed gap, not a guess.
- `id_token` claims Codex reads (from `codex-rs/login/src/token_data.rs`, `IdClaims`/`AuthClaims`):
  standard `email`, plus a custom namespace `https://api.openai.com/auth` carrying
  `chatgpt_plan_type`, `chatgpt_user_id`/`user_id`, `chatgpt_account_id`,
  `chatgpt_account_is_fedramp`.

### On-disk shape (`~/.codex/auth.json`)

```rust
struct AuthDotJson {
    auth_mode: Option<AuthMode>,
    #[serde(rename = "OPENAI_API_KEY")]
    openai_api_key: Option<String>,
    tokens: Option<TokenData>,          // { id_token, access_token, refresh_token, account_id }
    last_refresh: Option<DateTime<Utc>>,
    agent_identity: Option<AgentIdentityStorage>,
    personal_access_token: Option<String>,
    bedrock_api_key: Option<BedrockApiKeyAuth>,
}
```
Source: `codex-rs/login/src/auth/storage.rs` (`AuthDotJson`) and
`codex-rs/login/src/token_data.rs` (`TokenData`). Ticket's premise that Codex stores results in
`~/.codex/auth.json` is confirmed structurally by this type (the actual file path
`~/.codex/auth.json` itself is set by `codex_home` resolution elsewhere in `codex-core`, not
re-verified line-by-line here, but `persist_tokens_async` in `server.rs` writes exactly this
`AuthDotJson` shape via `save_auth(&opts.codex_home, ...)`).

## 6. Refresh flow

- **Endpoint:** same token endpoint, `POST https://auth.openai.com/oauth/token`.
- **Request:** unlike the code-exchange call (form-urlencoded), the refresh call sends
  **JSON** body:
  ```json
  { "client_id": "app_EMoamEEZ73f0CkXaXp7hrann", "grant_type": "refresh_token", "refresh_token": "<token>" }
  ```
  Source: `codex-rs/login/src/auth/manager.rs`, `RefreshRequest` struct and
  `request_chatgpt_token_refresh`.
- **Response:** JSON with all three fields optional:
  ```rust
  struct RefreshResponse {
      id_token: Option<String>,
      access_token: Option<String>,
      refresh_token: Option<String>,
  }
  ```
  Codex persists whichever fields are present, overwriting the stored value
  (`persist_tokens` in `manager.rs`). Concretely: **when the response includes a new
  `refresh_token`, Codex replaces the stored one with it** — i.e. the client is written to handle
  rotation.
- **Evidence the server does rotate refresh tokens (reuse detection):** Codex classifies token
  endpoint error codes for a failed refresh into three reasons
  (`classify_refresh_token_failure`, `codex-rs/login/src/auth/manager.rs`):
  - `refresh_token_expired` → `Expired`
  - `refresh_token_reused` → `Exhausted`
  - `refresh_token_invalidated` → `Revoked`
  The existence of a distinct `refresh_token_reused` server error code that Codex specifically
  handles is strong evidence the auth server invalidates a refresh token after its first use and
  rejects any later reuse of the same token — i.e. rotating refresh tokens with reuse detection.
  This is inferred from the client's explicit handling of that error code, not from a document
  that states "refresh tokens rotate" in so many words; flagging that distinction rather than
  overclaiming it as directly documented.
- **Proactive refresh trigger** (client-side policy, not a server-stated lifetime):
  `should_refresh_proactively()` in `manager.rs` refreshes when the access-token JWT's `exp` is
  within `CHATGPT_ACCESS_TOKEN_REFRESH_WINDOW_MINUTES = 5` minutes of expiring. If the access
  token's `exp` can't be parsed, it falls back to refreshing when `last_refresh` is older than
  `TOKEN_REFRESH_INTERVAL = 8` days. This 8-day number is Codex's own conservative fallback
  heuristic, not a published server-side refresh-token lifetime — do not treat it as "refresh
  tokens last 8 days."

## 7. Bonus, directly relevant to relay's `chatgpt` backend: API-key bridging

After completing the OAuth code exchange, Codex immediately does a second call to the **same**
token endpoint using an RFC 8693 token-exchange grant to mint a platform-style API key from the
just-obtained `id_token`:

```
grant_type=urn:ietf:params:oauth:grant-type:token-exchange
&client_id=app_EMoamEEZ73f0CkXaXp7hrann
&requested_token=openai-api-key
&subject_token=<id_token>
&subject_token_type=urn:ietf:params:oauth:token-type:id_token
```
Response: `{ "access_token": "<api-key-shaped token>" }`, stored as `OPENAI_API_KEY` in
`auth.json`.
Source: `codex-rs/login/src/server.rs`, function `obtain_api_key`, called from
`process_request`/`persist_tokens_async` right after the OAuth code exchange succeeds.

This means Codex's "Sign in with ChatGPT" flow does not, in the end, force all downstream API
calls to use the raw ChatGPT OAuth access token — it also derives a conventional-looking API key
via a token-exchange grant, which is likely how Codex reuses existing API-key-shaped request code
paths after ChatGPT login. Relevant if relay's `chatgpt` backend wants to reuse
`Clients/OpenAiClient.cs`'s existing bearer-token plumbing instead of writing a parallel client.

## 8. Failure modes worth designing around

- **Abandoned browser flow (user never completes sign-in):** `run_login_server` has **no
  built-in timeout**. The callback server's main loop (`tokio::select!` over `shutdown_notify` and
  incoming requests) will wait indefinitely until either (a) a callback request arrives, (b) an
  HTTP `GET /cancel` request arrives, or (c) the caller explicitly calls
  `LoginServer::cancel()` / drops the shutdown handle (e.g. on Ctrl-C in the CLI). There is no
  server-enforced expiry coded in this crate for the interactive browser flow.
  Source: `codex-rs/login/src/server.rs`, `run_login_server`, `HandledRequest::ResponseAndExit`
  case for path `"/cancel"`.
  — By contrast, the **device-code fallback** flow (see below) *does* have a hardcoded max wait of
  15 minutes.

- **Loopback port already in use:** see §4 binding algorithm — Codex first tries to send a
  `/cancel` to the busy port assuming it's a stale prior instance, retries for up to ~2 seconds
  (10 × 200ms), then falls back to a second fixed port (1457), and only then surfaces an
  `AddrInUse` error to the caller if neither port is available.

- **State mismatch on callback:** any callback whose `state` query param doesn't match the
  original generated `state` gets a `400 "State mismatch"` response and the flow errors out —
  CSRF protection. Source: `process_request`, path `"/auth/callback"`, `state_valid` check.

- **OAuth error callback** (`?error=...&error_description=...`): surfaced via
  `oauth_callback_error_message`. One error is special-cased: `error=access_denied` with an
  `error_description` containing `missing_codex_entitlement` renders a distinct "Codex is not
  enabled for your workspace" page rather than a generic failure message. All other errors render
  `"Sign-in failed: {description or error_code}"`.

- **Token endpoint returns non-2xx during code exchange:** body is parsed for `error`/
  `error_description` (also handles a nested `{"error": {"code", "message"}}` shape) and surfaced
  to the user as `"Token exchange failed: {detail}"`. Source: `parse_token_endpoint_error` and its
  unit tests in `codex-rs/login/src/server.rs`.

- **Refresh failures are classified into user-facing categories** (see §6): expired, reused
  (rotation-detected reuse), revoked, or unknown — each maps to a distinct message telling the
  user to log out and sign in again. No explicit rate-limit handling/backoff code was found for
  the refresh or token endpoints in this crate (i.e. Codex does not appear to special-case HTTP
  429 from the auth server) — flagging this as an unconfirmed/likely-absent behavior rather than
  asserting a specific rate limit, since no rate-limit numbers are stated anywhere in this source.

- **Device-code fallback flow exists** for headless/browserless login (relevant to
  `06-headless-login-fallback.md`), against the same issuer:
  - `POST https://auth.openai.com/api/accounts/deviceauth/usercode` `{client_id}` →
    `{device_auth_id, user_code, interval}`
  - `POST https://auth.openai.com/api/accounts/deviceauth/token`, polled every `interval` seconds
    with `{device_auth_id, user_code}`, **hardcoded 15-minute max wait**
    (`Duration::from_secs(15 * 60)`), erroring `"device auth timed out after 15 minutes"` if
    exceeded.
  - On success, returns `{authorization_code, code_challenge, code_verifier}`, which is then run
    through the **same** `exchange_code_for_tokens` code path as the browser flow, with
    `redirect_uri = "{issuer}/deviceauth/callback"`.
  Source: `codex-rs/login/src/device_code_auth.rs`.

## Files consulted (all in `openai/codex`, commit `c8957bbf`)

- `codex-rs/login/src/server.rs` — authorize URL, callback server, port binding/fallback, code
  exchange, API-key token-exchange, error handling
- `codex-rs/login/src/pkce.rs` — PKCE verifier/challenge generation
- `codex-rs/login/src/auth/manager.rs` — client id constant, refresh request/response, proactive
  refresh policy, refresh-failure classification, revoke/refresh endpoint constants
- `codex-rs/login/src/auth/revoke.rs` — logout/revocation request shape
- `codex-rs/login/src/auth/storage.rs` — `AuthDotJson` (on-disk `auth.json` shape)
- `codex-rs/login/src/token_data.rs` — `TokenData`, JWT claim parsing, JWT expiry parsing
- `codex-rs/login/src/device_code_auth.rs` — headless/device-code fallback flow
- `codex-rs/cli/src/login.rs` — how `codex login` wires `ServerOptions` and blocks on the server
- `codex-rs/tui/src/onboarding/auth.rs` — corroborates client id literal used in TUI onboarding

## Explicitly unconfirmed (do not treat as fact)

- The actual numeric lifetime of an access token (e.g. "X minutes") — not stated in Codex source;
  only derivable at runtime from the JWT `exp` claim issued by the server.
- Whether refresh tokens rotate on *every* single use, or only sometimes — inferred from the
  client's handling of a `refresh_token_reused` error code and from `refresh_token` being
  `Option`al-but-typically-present in refresh responses, not from an explicit OpenAI statement.
- Any rate limits on the authorize/token/refresh endpoints — no rate-limit-specific handling (e.g.
  429 backoff) was found in this crate.
- The exact `~/.codex/auth.json` file path/permissions logic (e.g. `codex_home` resolution, file
  mode) was not traced into `codex-core`; only the JSON shape written to it was confirmed here.
