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
`PostgresMemoryClient.ProcessNowAsync` when the application owns processing cadence explicitly.

The database role must be able to create the configured schema and tables when
`EnsureSchemaOnFirstUse` is enabled. Install the PostgreSQL `vector` extension before deployment,
or grant the development role permission to create it on first use.

To run the live storage tests, set `POSTGRES_MEMORY_CONNECTION_STRING` to a PostgreSQL database with
pgvector available, then run the `Category=Postgres` tests.
