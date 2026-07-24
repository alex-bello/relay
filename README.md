# RelayAgent

A minimal agent harness in .NET 10, compiled with Native AOT. Two backends
(Anthropic Messages API and any OpenAI-compatible endpoint) behind one
abstraction, one tool, and a loop.

The assembly is named `relay`, so the published binary is `relay` even though
the namespace and project are `RelayAgent`. Unambiguous where it needs to be,
short where you actually type it.

## Reading order

Read the files in this order — it's arranged as an argument, not a codebase.

1. **`src/Agent.cs`** — the loop. Start here. It's forty lines and it's the
   entire concept. Everything else is plumbing in service of it.
2. **`src/Domain.cs`** — the neutral model, and why it has to exist.
3. **`src/Anthropic/AnthropicClient.cs`** and **`src/OpenAI/OpenAiClient.cs`** —
   read the two `ToWire` methods side by side. The diff between them is the
   answer to "why not just use the provider SDK?"
4. **`src/Tools/Tools.cs`** — schemas, dispatch, and error handling.

## Build and run

```bash
dotnet build

# Anthropic
export ANTHROPIC_API_KEY=sk-...
export RELAY_BACKEND=anthropic
dotnet run -- /path/to/workspace

# Local, OpenAI-compatible
export RELAY_BACKEND=local
export OPENAI_BASE_URL=http://vm-llm:8080/
dotnet run -- /path/to/workspace

# Native AOT
dotnet publish -c Release -r osx-arm64
```

The published binary should land around 8–12 MB with no runtime dependency and
a cold start in single-digit milliseconds. That's the payoff for the JSON
discipline.

## Native AOT notes

- `JsonSerializerIsReflectionEnabledByDefault=false` in the csproj makes
  reflection-based serialization throw at runtime in **debug** builds too. This
  turns "mystery failure after publish" into "obvious failure on first run."
  Leave it on.
- Every serialized type must be reachable from a `JsonSerializerContext`.
- `JsonDocument` / `JsonElement` are readers, not deserializers, and are always
  AOT-safe. That's why tool schemas and tool arguments use them.
- Polymorphic `WireContent` deserialization depends on the `type` discriminator.
  System.Text.Json handles it appearing out of order by buffering, but it is the
  first thing to check if content blocks come back wrong.
- `LibraryImport` over `DllImport` if you ever add native interop.

## Deliberately missing

Each of these is a good next milestone, roughly in order of payoff:

- **Streaming.** SSE parsing with `Utf8JsonReader` over a chunked stream.
  Changes the feel more than anything else on this list.
- **A tool source generator.** Read `[Tool]`-attributed records at compile time,
  emit both the JSON Schema and the dispatch table. This is the AOT-native
  answer to the reflection scanning that pi does at runtime, and writing it will
  teach you more about the .NET compiler than about LLMs.
- **Context compaction.** When the transcript approaches the window, summarize
  the middle and keep the head and tail. Watch what breaks — it's instructive.
- **Permissions.** A prompt before any mutating tool runs.
- **Token accounting.** Read `usage` off the responses and print a running cost.
- **A `write_file` and `bash` tool**, at which point sandboxing stops being
  optional.

## A caveat on model IDs

`Program.cs` defaults to a model string that may be outdated by the time you run
this. Check the provider's current model list and set `RELAY_MODEL`.
