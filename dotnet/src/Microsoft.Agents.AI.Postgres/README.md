# Microsoft Agent Framework PostgreSQL ContextMemory

`Microsoft.Agents.AI.Postgres` provides durable, cross-session agent memory backed by PostgreSQL
and pgvector. The package separates memory processing from Agent Framework lifecycle integration:

- `PostgresMemoryClient` owns the memory model, hybrid retrieval, processing pipeline, and
  background tasks.
- `PostgresMemoryContextProvider` adapts the client to Agent Framework's before/after invocation
  lifecycle.

The client stores raw turns and derives facts, procedural memories, episodic memories, thread
summaries, and cross-thread user summaries. Derived memories include confidence metadata and can be
reconciled when newer information contradicts older records.

```csharp
await using var dataSource = new NpgsqlDataSourceBuilder(connectionString)
    .UseVector()
    .Build();

await using var memoryProvider = new PostgresMemoryContextProvider(
    dataSource,
    embeddingGenerator,
    chatClient,
    _ => new PostgresMemoryContextProvider.State(
        new PostgresMemoryScope
        {
            UserId = authenticatedUserId,
            ThreadId = conversationId,
        }),
    clientOptions: new PostgresMemoryClientOptions
    {
        EmbeddingDimensions = 1536,
    },
    providerOptions: new PostgresMemoryContextProviderOptions
    {
        TopK = 5,
    });

var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    AIContextProviders = [memoryProvider],
});
```

This constructor creates, flushes, and disposes the internal `PostgresMemoryClient`. The supplied
`NpgsqlDataSource`, embedding generator, and chat client remain owned by the caller or dependency
injection container. Create a `PostgresMemoryClient` separately and pass it to the provider when it
must be shared, queried directly, or used by an external processing worker.

Use a stable, authorized `UserId` for cross-session recall. The default search scope omits the
thread id, so relevant memories can be recalled from the user's other conversations.
`ApplicationId` and `AgentId` are exact isolation dimensions: a null value matches only records
stored with a null value and does not search other applications or agents.

Turn writes schedule extraction and summary processing in the background by default. Drain pending
work before shutdown:

```csharp
await memoryProvider.FlushAsync();
```

Set `PostgresMemoryClientOptions.AutoProcess` to `false` and call
`PostgresMemoryContextProvider.ProcessNowAsync` with the current agent session, or call
`PostgresMemoryClient.ProcessNowAsync` with a scope when the application owns processing cadence
explicitly.

The database role must be able to create the configured schema and tables when
`EnsureSchemaOnFirstUse` is enabled. Install the PostgreSQL `vector` extension before deployment,
or grant the development role permission to create it on first use.

## Azure PostgreSQL DiskANN

Azure Database for PostgreSQL flexible server can use the `pg_diskann` extension instead of HNSW
for approximate vector search. Allowlist both `vector` and `pg_diskann` in the server's
`azure.extensions` parameter, then select DiskANN in the client options:

```csharp
clientOptions: new PostgresMemoryClientOptions
{
    EmbeddingDimensions = 1536,
    VectorIndexKind = PostgresMemoryVectorIndexKind.DiskAnn,
}
```

When `EnsureSchemaOnFirstUse` is enabled, the client creates the allowlisted extensions and the
selected indexes. Changing `VectorIndexKind` replaces indexes managed by this package; it does not
modify other indexes. DiskANN availability and supported versions depend on the Azure region and
PostgreSQL version. HNSW remains the default and works with PostgreSQL installations that support
pgvector.

## Azure AI semantic reranking

Azure Database for PostgreSQL flexible server can rerank the hybrid search candidate set by using
the preview `azure_ai.rank()` function. Allowlist `azure_ai`, deploy a supported reranker in
Microsoft Foundry, and configure the extension's endpoint and authentication. Managed identity is
recommended.

Enable reranking in the client options:

```csharp
clientOptions: new PostgresMemoryClientOptions
{
    EnableAzureAiReranking = true,
    AzureAiRerankerModel = "cohere-rerank-v3.5",
    RerankingCandidateCount = 25,
}
```

The client retrieves candidates with vector and full-text search, combines them with reciprocal
rank fusion, and sends at most `RerankingCandidateCount` records to `azure_ai.rank()`. It returns
the requested `TopK` in semantic rank order. `PostgresMemoryRecord.Score` retains the reciprocal
rank fusion score, and `PostgresMemoryRecord.RerankerScore` contains the semantic relevance score.
If the ranking call fails with a PostgreSQL error, the client logs a warning and returns the
original hybrid order.

To run the live storage tests, set `POSTGRES_MEMORY_CONNECTION_STRING` to a PostgreSQL database with
pgvector available, then run the `Category=Postgres` tests.
