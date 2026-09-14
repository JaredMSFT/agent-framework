// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Moq;

namespace Microsoft.Agents.AI.Postgres.UnitTests;

/// <summary>
/// Tests for <see cref="PostgresMemoryClient"/>.
/// </summary>
public sealed class PostgresMemoryClientTests
{
    private static readonly ReadOnlyMemory<float> s_embedding = new([0.1f, 0.2f, 0.3f]);

    private readonly Mock<IPostgresMemoryStore> _store = new();
    private readonly Mock<IEmbeddingGenerator<string, Embedding<float>>> _embeddingGenerator = new();
    private readonly Mock<IChatClient> _chatClient = new();

    public PostgresMemoryClientTests()
    {
        this._embeddingGenerator
            .Setup(generator => generator.GenerateAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<EmbeddingGenerationOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) =>
                new GeneratedEmbeddings<Embedding<float>>(
                    values.Select(_ => new Embedding<float>(s_embedding))));
        this._store
            .Setup(store => store.GetProcessingStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(default(ProcessingState));
        this._store
            .Setup(store => store.GetActiveMemoriesAsync(
                It.IsAny<PostgresMemoryScope>(),
                It.IsAny<IReadOnlyList<PostgresMemoryType>>(),
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        this._store
            .Setup(store => store.GetRecentThreadSummariesAsync(
                It.IsAny<PostgresMemoryScope>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        this._store
            .Setup(store => store.GetLatestSummaryAsync(
                It.IsAny<PostgresMemoryScope>(),
                It.IsAny<PostgresMemoryType>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((null, 0L));
    }

    [Fact]
    public async Task UpsertMemoryAsync_NormalizesAssistantRoleAsync()
    {
        this._store
            .Setup(store => store.InsertTurnAsync(
                It.IsAny<PostgresMemoryScope>(),
                "agent",
                "hello",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);
        var client = this.CreateClient(new PostgresMemoryClientOptions { AutoProcess = false });

        var id = await client.UpsertMemoryAsync(CreateScope(), "assistant", " hello ");

        Assert.Equal(42, id);
        this._store.Verify(store => store.EnsureSchemaAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_EmbedsQueryAndPassesRetrievalFiltersAsync()
    {
        this._store
            .Setup(store => store.SearchAsync(
                It.IsAny<PostgresMemoryScope>(),
                "postgres preferences",
                s_embedding,
                It.Is<IReadOnlyList<PostgresMemoryType>>(types =>
                    types.SequenceEqual(new[] { PostgresMemoryType.Fact })),
                7,
                0.8,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var client = this.CreateClient();

        _ = await client.SearchAsync(
            CreateScope(),
            "postgres preferences",
            [PostgresMemoryType.Fact],
            7,
            0.8);

        this._embeddingGenerator.Verify(
            generator => generator.GenerateAsync(
                It.Is<IEnumerable<string>>(values => values.Single() == "postgres preferences"),
                It.IsAny<EmbeddingGenerationOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractMemoriesAsync_StoresTypedMemoriesAndSkipsDuplicateAsync()
    {
        var turns = new[]
        {
            CreateRecord(1, PostgresMemoryType.Turn, "I prefer PostgreSQL.", role: "user", threadId: "thread"),
        };
        this._store
            .Setup(store => store.GetTurnsAfterAsync(
                It.IsAny<PostgresMemoryScope>(),
                0,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(turns);
        this._chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                """
                [
                  {"content":"Prefers PostgreSQL","memory_type":"fact","confidence":0.96},
                  {"content":"Show SQL first","memory_type":"procedural","confidence":0.88}
                ]
                """)));
        this._store
            .Setup(store => store.FindDuplicateAsync(
                It.IsAny<PostgresMemoryScope>(),
                PostgresMemoryType.Procedural,
                It.IsAny<string>(),
                It.IsAny<ReadOnlyMemory<float>>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRecord(9, PostgresMemoryType.Procedural, "Show SQL first"));
        var client = this.CreateClient();

        var inserted = await client.ExtractMemoriesAsync(CreateScope());

        Assert.Equal(1, inserted);
        this._store.Verify(
            store => store.InsertDerivedMemoryAsync(
                It.IsAny<PostgresMemoryScope>(),
                PostgresMemoryType.Fact,
                "Prefers PostgreSQL",
                0.96,
                null,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ReadOnlyMemory<float>>(),
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
        this._store.Verify(
            store => store.InsertDerivedMemoryAsync(
                It.IsAny<PostgresMemoryScope>(),
                PostgresMemoryType.Procedural,
                It.IsAny<string>(),
                It.IsAny<double>(),
                It.IsAny<double?>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ReadOnlyMemory<float>>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExtractMemoriesAsync_AppliesEpisodicTimeToLiveAsync()
    {
        this._store
            .Setup(store => store.GetTurnsAfterAsync(
                It.IsAny<PostgresMemoryScope>(),
                0,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateRecord(1, PostgresMemoryType.Turn, "The migration succeeded.", role: "user", threadId: "thread"),
            ]);
        this._chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                """[{"content":"Migration succeeded after adding an index","memory_type":"episodic","confidence":0.9}]""")));
        var client = this.CreateClient(new PostgresMemoryClientOptions
        {
            AutoProcess = false,
            EpisodicMemoryTimeToLive = TimeSpan.FromDays(30),
        });

        _ = await client.ExtractMemoriesAsync(CreateScope());

        this._store.Verify(
            store => store.InsertDerivedMemoryAsync(
                It.IsAny<PostgresMemoryScope>(),
                PostgresMemoryType.Episodic,
                It.IsAny<string>(),
                It.IsAny<double>(),
                It.IsAny<double?>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ReadOnlyMemory<float>>(),
                It.Is<DateTimeOffset?>(value =>
                    value.HasValue && value.Value > DateTimeOffset.UtcNow.AddDays(29)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractMemoriesAsync_MalformedResponseDoesNotAdvanceCheckpointAsync()
    {
        this._store
            .Setup(store => store.GetTurnsAfterAsync(
                It.IsAny<PostgresMemoryScope>(),
                0,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateRecord(1, PostgresMemoryType.Turn, "Remember this.", role: "user", threadId: "thread"),
            ]);
        this._chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not valid JSON")));
        var client = this.CreateClient();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ExtractMemoriesAsync(CreateScope()));

        this._store.Verify(
            store => store.UpsertProcessingStateAsync(
                It.IsAny<string>(),
                It.IsAny<ProcessingState>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateThreadSummaryAsync_InsertsIncrementalSummaryAsync()
    {
        this._store
            .Setup(store => store.GetTurnsAfterAsync(
                It.IsAny<PostgresMemoryScope>(),
                0,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateRecord(5, PostgresMemoryType.Turn, "Use Postgres.", role: "user", threadId: "thread"),
            ]);
        this._chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The user chose PostgreSQL.")));
        this._store
            .Setup(store => store.InsertSummaryAsync(
                It.IsAny<PostgresMemoryScope>(),
                PostgresMemoryType.Summary,
                It.IsAny<string>(),
                It.IsAny<ReadOnlyMemory<float>>(),
                5,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(10);
        var client = this.CreateClient();

        var summary = await client.GenerateThreadSummaryAsync(CreateScope());

        Assert.NotNull(summary);
        Assert.Equal(PostgresMemoryType.Summary, summary.MemoryType);
        Assert.Equal("The user chose PostgreSQL.", summary.Content);
    }

    [Fact]
    public async Task ReconcileAsync_MarksOnlyValidContradictionsAsync()
    {
        var memories = new[]
        {
            CreateRecord(1, PostgresMemoryType.Fact, "User is vegetarian."),
            CreateRecord(2, PostgresMemoryType.Fact, "User eats steak."),
        };
        this._store
            .Setup(store => store.GetActiveMemoriesAsync(
                It.IsAny<PostgresMemoryScope>(),
                It.IsAny<IReadOnlyList<PostgresMemoryType>>(),
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);
        this._chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                """[{"superseded_id":1,"winner_id":2,"reason":"contradict"}]""")));
        var client = this.CreateClient();

        var reconciled = await client.ReconcileAsync(CreateScope());

        Assert.Equal(1, reconciled);
        this._store.Verify(
            store => store.MarkSupersededAsync(1, 2, "contradict", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_MalformedResponseThrowsAsync()
    {
        this._store
            .Setup(store => store.GetActiveMemoriesAsync(
                It.IsAny<PostgresMemoryScope>(),
                It.IsAny<IReadOnlyList<PostgresMemoryType>>(),
                It.IsAny<int>(),
                It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateRecord(1, PostgresMemoryType.Fact, "User is vegetarian."),
                CreateRecord(2, PostgresMemoryType.Fact, "User eats steak."),
            ]);
        this._chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not valid JSON")));
        var client = this.CreateClient();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ReconcileAsync(CreateScope()));

        this._store.Verify(
            store => store.MarkSupersededAsync(
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AutoProcess_SchedulesBackgroundExtractionAndFlushWaitsAsync()
    {
        this._store
            .Setup(store => store.InsertTurnAsync(
                It.IsAny<PostgresMemoryScope>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<ReadOnlyMemory<float>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        this._store
            .Setup(store => store.GetTurnStatsAfterAsync(
                It.IsAny<PostgresMemoryScope>(),
                It.IsAny<long>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((1, 1L));
        this._store
            .Setup(store => store.GetTurnsAfterAsync(
                It.IsAny<PostgresMemoryScope>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateRecord(1, PostgresMemoryType.Turn, "hello", role: "user", threadId: "thread"),
            ]);
        this._chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));
        var client = this.CreateClient(new PostgresMemoryClientOptions
        {
            AutoProcess = true,
            FactExtractionEveryNTurns = 1,
            ThreadSummaryEveryNTurns = 0,
            UserSummaryEveryNTurns = 0,
            ReconcileEveryNExtractions = 0,
        });

        _ = await client.UpsertMemoryAsync(CreateScope(), "user", "hello");
        await client.FlushAsync();

        this._chatClient.Verify(
            chat => chat.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private PostgresMemoryClient CreateClient(PostgresMemoryClientOptions? options = null) =>
        new(
            this._store.Object,
            this._embeddingGenerator.Object,
            this._chatClient.Object,
            options ?? new PostgresMemoryClientOptions { AutoProcess = false });

    private static PostgresMemoryScope CreateScope() =>
        new() { ApplicationId = "app", AgentId = "agent", UserId = "user", ThreadId = "thread" };

    private static PostgresMemoryRecord CreateRecord(
        long id,
        PostgresMemoryType type,
        string content,
        string? role = null,
        string? threadId = null) =>
        new()
        {
            Id = id,
            MemoryType = type,
            Content = content,
            Role = role,
            UserId = "user",
            ThreadId = threadId,
            Confidence = type == PostgresMemoryType.Turn ? null : 0.9,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
}
