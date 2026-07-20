using FluentAssertions;
using HowsItGoing.Bridge.Services;
using HowsItGoing.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HowsItGoing.Bridge.Tests;

[TestFixture]
public sealed class SessionAndLaunchTests
{
    [Test]
    public async Task GetSessionsAsync_filters_in_sql_before_runtime_state_parsing()
    {
        var databasePath = Path.GetTempFileName();

        try
        {
            await CreateThreadsDatabaseAsync(
                databasePath,
                [
                    CreateThreadRow("thread-1", "Keep this session", @"C:\dev\howsitgoing"),
                    CreateThreadRow("thread-2", "Ignore this session", @"C:\dev\other")
                ]);

            var provider = new FakeRuntimeStateProvider();
            var service = CreateSessionService(databasePath, provider);

            var sessions = await service.GetSessionsAsync("keep", status: null, source: null, includeArchived: false, CancellationToken.None);

            sessions.Should().HaveCount(1);
            sessions[0].Id.Should().Be("thread-1");
            provider.CallCount.Should().Be(1);
        }
        finally
        {
            TryDelete(databasePath);
        }
    }

    [Test]
    public async Task GetSessionsAsync_returns_fallback_state_when_runtime_state_lookup_fails()
    {
        var databasePath = Path.GetTempFileName();
        var badRolloutPath = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.jsonl");
        var goodRolloutPath = Path.Combine(Path.GetTempPath(), $"good-{Guid.NewGuid():N}.jsonl");

        try
        {
            await CreateThreadsDatabaseAsync(
                databasePath,
                [
                    CreateThreadRow("bad-thread", "Bad rollout", @"C:\dev\howsitgoing", rolloutPath: badRolloutPath),
                    CreateThreadRow("good-thread", "Good rollout", @"C:\dev\howsitgoing", rolloutPath: goodRolloutPath)
                ]);

            var provider = new FakeRuntimeStateProvider
            {
                ThrowingRolloutPath = badRolloutPath,
                SuccessfulState = new CodexRuntimeState(CodexSessionStatus.Completed, "Done", DateTimeOffset.UtcNow)
            };

            var service = CreateSessionService(databasePath, provider);

            var sessions = await service.GetSessionsAsync(null, status: null, source: null, includeArchived: false, CancellationToken.None);

            sessions.Should().HaveCount(2);
            sessions.Single(x => x.Id == "bad-thread").LastAgentMessage.Should().BeNull();
            sessions.Single(x => x.Id == "good-thread").Status.Should().Be(CodexSessionStatus.Completed);
            sessions.Single(x => x.Id == "good-thread").LastAgentMessage.Should().Be("Done");
        }
        finally
        {
            TryDelete(databasePath);
            TryDelete(badRolloutPath);
            TryDelete(goodRolloutPath);
        }
    }

    [Test]
    public void CodexLaunchCommandBuilder_applies_defaults_and_uses_stdin_for_prompt()
    {
        var prompt = "Line 1 with \"quotes\"" + Environment.NewLine + "Line 2";
        var command = CodexLaunchCommandBuilder.Build(
            new StartCodexRunRequest(@"C:\dev\howsitgoing", prompt, null, null),
            @"C:\dev\howsitgoing",
            isGitRepository: true);

        command.PromptInput.Should().Be(prompt);
        command.Arguments.Should().ContainInOrder("exec", "--json", "--full-auto", "-C", @"C:\dev\howsitgoing", "--model", "gpt-5.4");
        command.Arguments.Should().Contain("-c");
        command.Arguments.Should().Contain("model_reasoning_effort=\"high\"");
        command.Arguments.Last().Should().Be("-");
        command.Arguments.Should().NotContain(prompt);
        command.Arguments.Should().NotContain(x => x.Contains("Line 1", StringComparison.Ordinal));
    }

    [Test]
    public async Task ThreadExistsAsync_returns_true_only_for_known_thread_ids()
    {
        var databasePath = Path.GetTempFileName();

        try
        {
            await CreateThreadsDatabaseAsync(
                databasePath,
                [
                    CreateThreadRow("thread-1", "Known thread", @"C:\dev\howsitgoing")
                ]);

            var service = CreateSessionService(databasePath, new FakeRuntimeStateProvider());

            (await service.ThreadExistsAsync("thread-1", CancellationToken.None)).Should().BeTrue();
            (await service.ThreadExistsAsync("missing-thread", CancellationToken.None)).Should().BeFalse();
        }
        finally
        {
            TryDelete(databasePath);
        }
    }

    [Test]
    [Platform("Win")]
    public void CodexCliLocator_prefers_npm_codex_js_with_node_over_windowsapps_binary()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"hig-codex-{Guid.NewGuid():N}");
        var appDataPath = Path.Combine(tempRoot, "appdata");
        var npmCodexDirectory = Path.Combine(appDataPath, "npm", "node_modules", "@openai", "codex", "bin");
        var nodeDirectory = Path.Combine(tempRoot, "bin");
        var windowsAppsDirectory = Path.Combine(tempRoot, "windowsapps");
        var originalAppData = Environment.GetEnvironmentVariable("APPDATA");
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        Directory.CreateDirectory(npmCodexDirectory);
        Directory.CreateDirectory(nodeDirectory);
        Directory.CreateDirectory(windowsAppsDirectory);
        File.WriteAllText(Path.Combine(npmCodexDirectory, "codex.js"), "// test");
        File.WriteAllText(Path.Combine(nodeDirectory, "node.exe"), string.Empty);
        File.WriteAllText(Path.Combine(windowsAppsDirectory, "codex.exe"), string.Empty);

        try
        {
            Environment.SetEnvironmentVariable("APPDATA", appDataPath);
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, [windowsAppsDirectory, nodeDirectory]));

            var launchSpec = CodexCliLocator.Resolve(new ConfigurationBuilder().Build());

            launchSpec.FileName.Should().Be(Path.Combine(nodeDirectory, "node.exe"));
            launchSpec.PrefixArguments.Should().ContainSingle()
                .Which.Should().Be(Path.Combine(npmCodexDirectory, "codex.js"));
            launchSpec.Mode.Should().Be(CodexLaunchMode.Direct);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", originalAppData);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            TryDelete(Path.Combine(npmCodexDirectory, "codex.js"));
            TryDelete(Path.Combine(nodeDirectory, "node.exe"));
            TryDelete(Path.Combine(windowsAppsDirectory, "codex.exe"));
            TryDeleteDirectory(tempRoot);
        }
    }

    private static CodexSessionService CreateSessionService(string databasePath, ICodexRuntimeStateProvider provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bridge:StateDbPath"] = databasePath
            })
            .Build();

        return new CodexSessionService(configuration, provider, NullLogger<CodexSessionService>.Instance);
    }

    private static async Task CreateThreadsDatabaseAsync(string databasePath, IReadOnlyList<ThreadSeed> rows)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();

        var create = connection.CreateCommand();
        create.CommandText = """
            create table threads (
                id text primary key,
                rollout_path text,
                created_at integer not null,
                updated_at integer not null,
                source text not null,
                cwd text not null,
                title text not null,
                archived integer not null,
                git_branch text,
                git_origin_url text,
                agent_nickname text,
                agent_role text
            );
            """;
        await create.ExecuteNonQueryAsync();

        foreach (var row in rows)
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                insert into threads (
                    id,
                    rollout_path,
                    created_at,
                    updated_at,
                    source,
                    cwd,
                    title,
                    archived,
                    git_branch,
                    git_origin_url,
                    agent_nickname,
                    agent_role)
                values (
                    @id,
                    @rollout_path,
                    @created_at,
                    @updated_at,
                    @source,
                    @cwd,
                    @title,
                    @archived,
                    @git_branch,
                    @git_origin_url,
                    @agent_nickname,
                    @agent_role);
                """;
            insert.Parameters.AddWithValue("@id", row.Id);
            insert.Parameters.AddWithValue("@rollout_path", (object?)row.RolloutPath ?? DBNull.Value);
            insert.Parameters.AddWithValue("@created_at", row.CreatedAt.ToUnixTimeSeconds());
            insert.Parameters.AddWithValue("@updated_at", row.UpdatedAt.ToUnixTimeSeconds());
            insert.Parameters.AddWithValue("@source", row.Source);
            insert.Parameters.AddWithValue("@cwd", row.Cwd);
            insert.Parameters.AddWithValue("@title", row.Title);
            insert.Parameters.AddWithValue("@archived", row.Archived ? 1 : 0);
            insert.Parameters.AddWithValue("@git_branch", (object?)row.GitBranch ?? DBNull.Value);
            insert.Parameters.AddWithValue("@git_origin_url", (object?)row.GitOriginUrl ?? DBNull.Value);
            insert.Parameters.AddWithValue("@agent_nickname", DBNull.Value);
            insert.Parameters.AddWithValue("@agent_role", DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static ThreadSeed CreateThreadRow(string id, string title, string cwd, string? rolloutPath = null) =>
        new(
            id,
            rolloutPath,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "vscode",
            cwd,
            title,
            false,
            "main",
            "https://github.com/marvijo-code/howsitgoing.git");

    private sealed class FakeRuntimeStateProvider : ICodexRuntimeStateProvider
    {
        public int CallCount { get; private set; }

        public string? ThrowingRolloutPath { get; init; }

        public CodexRuntimeState SuccessfulState { get; init; } = new(CodexSessionStatus.Running, "Working", null);

        public Task<CodexRuntimeState> GetRuntimeStateAsync(string? rolloutPath, DateTimeOffset updatedAt, bool archived, CancellationToken cancellationToken)
        {
            CallCount++;
            if (string.Equals(rolloutPath, ThrowingRolloutPath, StringComparison.Ordinal))
            {
                throw new IOException("boom");
            }

            return Task.FromResult(SuccessfulState);
        }

        public CodexRuntimeState GetFallbackState(DateTimeOffset updatedAt, bool archived) =>
            new(CodexSessionStatus.Running, null, null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed record ThreadSeed(
        string Id,
        string? RolloutPath,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string Source,
        string Cwd,
        string Title,
        bool Archived,
        string? GitBranch,
        string? GitOriginUrl);
}
