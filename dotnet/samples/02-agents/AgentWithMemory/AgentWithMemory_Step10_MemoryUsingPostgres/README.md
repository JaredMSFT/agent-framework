# Agent with Memory Using PostgreSQL

This sample uses `PostgresMemoryClient` and `PostgresMemoryContextProvider` to derive and persist typed memories in PostgreSQL with pgvector, then recall them in a new agent session.

## Features Demonstrated

- Authenticating to Microsoft Foundry with `DefaultAzureCredential`
- Storing and inspecting raw conversation turns
- Extracting fact, procedural, and episodic memories
- Generating thread and cross-thread user summaries
- Using pgvector hybrid search to recall all three derived-memory types in new sessions
- Reconciling a changed preference while preserving supersession history
- Running the processing pipeline explicitly with `ProcessNowAsync`
- Using automatic background processing with `FlushAsync` in a smaller getting-started mode

## Prerequisites

1. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
2. A Microsoft Foundry project with:
   - A chat model deployment (the default is `gpt-5.4-mini`)
   - A `text-embedding-3-large` deployment with 3,072 dimensions
3. A PostgreSQL database with the [pgvector extension](https://github.com/pgvector/pgvector) available
4. A database role that can create the configured schema, tables, indexes, and the `vector` extension if it is not already installed
5. Azure CLI authentication (`az login`)

## Configuration

Set the following environment variables:

| Variable | Description | Default |
|---|---|---|
| `FOUNDRY_PROJECT_ENDPOINT` | Microsoft Foundry project endpoint | *(required)* |
| `POSTGRES_MEMORY_CONNECTION_STRING` | Npgsql connection string for the memory database | *(required)* |
| `FOUNDRY_MODEL` | Chat model deployment name | `gpt-5.4-mini` |
| `FOUNDRY_EMBEDDING_MODEL` | Embedding model deployment name | `text-embedding-3-large` |
| `FOUNDRY_EMBEDDING_DIMENSIONS` | Number of dimensions produced by the embedding deployment | `3072` |

## Run the Sample

Run the comprehensive walkthrough:

```bash
dotnet run
```

The walkthrough deliberately exercises all four PostgreSQL tables:

1. The first session stores raw turns, seeds fact, procedural, and episodic information, and explicitly runs extraction, thread summarization, user summarization, and reconciliation.
2. The second session uses hybrid retrieval to recall those memories from a new thread.
3. The third session changes the user's favorite animal from elephants to giraffes, then runs extraction and reconciliation to supersede the conflicting preference.
4. The final session verifies that a new thread receives the updated preference.

The sample prints the stored turns, active typed memories, summaries, and reconciliation count. Memory extraction and reconciliation use model output, so the exact records and wording can vary between runs.

### Simple Background-Processing Demo

For a smaller getting-started example, run:

```bash
dotnet run -- --simple
```

This mode uses the provider constructor that owns its `PostgresMemoryClient`. The first session stores an elephant preference and schedules extraction automatically. `FlushAsync` waits for that background work before a second session asks for a suitable joke.