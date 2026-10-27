using FluentAssertions;
using HowsItGoing.Bridge.Services;
using HowsItGoing.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace HowsItGoing.Bridge.Tests;

/// <summary>
/// The thread viewer is the only way to read a whole run on the phone, so these tests pin the two
/// things that make a transcript readable: the right roles in file order, and no injected
/// machine turns (heartbeats, environment context, instruction preambles) in the middle of it.
/// </summary>
[TestFixture]
public sealed class SessionThreadTests
{
    [Test]
    public async Task Codex_transcript_keeps_user_and_assistant_turns_and_drops_injected_ones()
    {
        var root = CreateRoot();
        try
        {
            var rolloutPath = Path.Combine(root, "rollout.jsonl");
            await File.WriteAllLinesAsync(rolloutPath,
            [
                Line("""{"timestamp":"2026-10-12T09:00:00Z","type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"You are Codex"}]}}"""),
                Line("""{"timestamp":"2026-10-12T09:00:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context><cwd>C:/dev</cwd></environment_context>"}]}}"""),
                Line("""{"timestamp":"2026-10-12T09:00:02Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Fix the feed"}]}}"""),
                Line("""{"timestamp":"2026-10-12T09:00:03Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"On it."}]}}"""),
                Line("""{"timestamp":"2026-10-12T09:00:04Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"<heartbeat>."}]}}"""),
                Line("""{"timestamp":"2026-10-12T09:00:04Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"<heartbeat>\n<automation_id>social-post-follow-up</automation_id>\n<decision>DONT_NOTIFY</decision>\n</heartbeat>\n<oai-mem-citation>MEMORY.md:11</oai-mem-citation>"}]}}"""),
                Line("""{"timestamp":"2026-10-12T09:00:05Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Thanks"}]}}""")
            ]);

            var service = CreateService(root, stateDbPath: CreateCodexStateDatabase(root, rolloutPath));
            var thread = await service.GetThreadAsync(AgentKinds.Codex, "thread-1", limit: 20, before: null, CancellationToken.None);

            thread.Should().NotBeNull();
            thread!.TotalCount.Should().Be(3);
            thread.Messages.Select(message => message.Role).Should().Equal(
                SessionMessageRole.User,
                SessionMessageRole.Assistant,
                SessionMessageRole.User);
            thread.Messages[0].Text.Should().Be("Fix the feed");
            thread.Messages[1].Text.Should().Be("On it.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Test]
    public async Task Claude_transcript_strips_injected_context_and_collapses_tool_runs()
    {
        var root = CreateRoot();
        try
        {
            var projectDir = Path.Combine(root, "projects", "C--dev-demo");
            Directory.CreateDirectory(projectDir);
            await File.WriteAllLinesAsync(Path.Combine(projectDir, "session-1.jsonl"),
            [
                Line("""{"type":"user","timestamp":"2026-10-12T09:00:00Z","cwd":"C:/dev/demo","gitBranch":"main","message":{"role":"user","content":"<system-reminder>ignore me</system-reminder>Ship the fix"}}"""),
                Line("""{"type":"assistant","timestamp":"2026-10-12T09:00:01Z","message":{"role":"assistant","content":[{"type":"tool_use","name":"Read","input":{}}]}}"""),
                Line("""{"type":"assistant","timestamp":"2026-10-12T09:00:02Z","message":{"role":"assistant","content":[{"type":"tool_use","name":"Bash","input":{}}]}}"""),
                Line("""{"type":"assistant","timestamp":"2026-10-12T09:00:03Z","message":{"role":"assistant","content":[{"type":"text","text":"Fixed it."}]}}""")
            ]);

            var service = CreateService(root);
            var thread = await service.GetThreadAsync(AgentKinds.ClaudeCode, "session-1", limit: 20, before: null, CancellationToken.None);

            thread.Should().NotBeNull();
            thread!.TotalCount.Should().Be(3);
            thread.Messages[0].Text.Should().Be("Ship the fix");
            thread.Messages[1].Role.Should().Be(SessionMessageRole.Tool);
            thread.Messages[1].Text.Should().Be("Ran Read, Bash");
            thread.Messages[2].Text.Should().Be("Fixed it.");
            thread.WorkingDirectory.Should().Be("C:/dev/demo");
            thread.GitBranch.Should().Be("main");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Test]
    public async Task OpenCode_transcript_reads_v2_messages_and_returns_the_newest_window()
    {
        var root = CreateRoot();
        try
        {
            var databasePath = Path.Combine(root, "opencode.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
            {
                connection.Open();
                using var create = connection.CreateCommand();
                create.CommandText = """
                    create table session_v2 (
                        id text primary key, parent_id text, directory text, title text,
                        time_created integer not null, time_updated integer not null, time_archived integer);
                    create table session_message (
                        id text primary key, session_id text not null, type text not null,
                        seq integer, time_created integer not null, time_updated integer not null, data text not null);
                    """;
                create.ExecuteNonQuery();

                using var session = connection.CreateCommand();
                session.CommandText = """
                    insert into session_v2 (id, parent_id, directory, title, time_created, time_updated)
                    values ('ses_1', null, 'C:/dev/demo', 'Demo run', 1, 2);
                    """;
                session.ExecuteNonQuery();

                for (var i = 0; i < 5; i++)
                {
                    using var message = connection.CreateCommand();
                    message.CommandText = """
                        insert into session_message (id, session_id, type, seq, time_created, time_updated, data)
                        values (@id, 'ses_1', @type, @seq, @at, @at, @data);
                        """;
                    message.Parameters.AddWithValue("@id", $"msg_{i}");
                    message.Parameters.AddWithValue("@type", i % 2 == 0 ? "user" : "assistant");
                    message.Parameters.AddWithValue("@seq", i);
                    message.Parameters.AddWithValue("@at", 1000 + i);
                    message.Parameters.AddWithValue(
                        "@data",
                        $$"""{"content":[{"type":"text","text":"turn {{i}}"}]}""");
                    message.ExecuteNonQuery();
                }
            }

            var service = CreateService(root, openCodeDbPath: databasePath);
            var thread = await service.GetThreadAsync(AgentKinds.OpenCode, "ses_1", limit: 2, before: null, CancellationToken.None);

            thread.Should().NotBeNull();
            thread!.TotalCount.Should().Be(5);
            thread.StartIndex.Should().Be(3);
            thread.Messages.Select(message => message.Text).Should().Equal("turn 3", "turn 4");
            thread.HasEarlierMessages.Should().BeTrue();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Test]
    public async Task Unknown_session_returns_null_instead_of_throwing()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root, stateDbPath: CreateCodexStateDatabase(root, rolloutPath: null));
            var thread = await service.GetThreadAsync(AgentKinds.Codex, "missing", limit: 10, before: null, CancellationToken.None);

            thread.Should().BeNull();
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static SessionThreadService CreateService(
        string root,
        string? stateDbPath = null,
        string? openCodeDbPath = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bridge:CodexHome"] = Path.Combine(root, "codex"),
                ["Bridge:StateDbPath"] = stateDbPath ?? Path.Combine(root, "missing-state.sqlite"),
                ["Bridge:ClaudeHome"] = root,
                ["Bridge:OpenCodeDbPath"] = openCodeDbPath ?? Path.Combine(root, "missing-opencode.db")
            })
            .Build();

        return new SessionThreadService(configuration, NullLogger<SessionThreadService>.Instance);
    }

    private static string CreateCodexStateDatabase(string root, string? rolloutPath)
    {
        var databasePath = Path.Combine(root, "state.sqlite");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                create table threads (
                    id text primary key, rollout_path text, title text, cwd text, git_branch text);
                """;
            create.ExecuteNonQuery();
        }

        if (rolloutPath is not null)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                insert into threads (id, rollout_path, title, cwd, git_branch)
                values ('thread-1', @rollout, 'Demo thread', 'C:/dev/demo', 'main');
                """;
            insert.Parameters.AddWithValue("@rollout", rolloutPath);
            insert.ExecuteNonQuery();
        }

        return databasePath;
    }

    private static string Line(string json) => json;

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"howsitgoing-thread-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
