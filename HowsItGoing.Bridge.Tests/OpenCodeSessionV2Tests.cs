using FluentAssertions;
using HowsItGoing.Bridge.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace HowsItGoing.Bridge.Tests;

/// <summary>
/// OpenCode 2.x moved its store to <c>session_v2</c> + <c>session_message</c>. Reading only the 1.x
/// tables leaves the feed on month-old rows, which is what made every fresh run look missing.
/// </summary>
[TestFixture]
public sealed class OpenCodeSessionV2Tests
{
    [Test]
    public async Task Reads_sessions_and_the_last_reply_from_the_v2_tables()
    {
        var databasePath = CreateV2Database(new Seed("ses_v2_run", "OpenCode v2 run", "V2_REPLY_OK"));

        try
        {
            var sessions = await CreateService(databasePath)
                .GetSessionsAsync(includeArchived: false, CancellationToken.None);

            sessions.Should().HaveCount(1);
            sessions[0].Id.Should().Be("ses_v2_run");
            sessions[0].Title.Should().Be("OpenCode v2 run");
            sessions[0].LastAgentMessage.Should().Be("V2_REPLY_OK");
        }
        finally
        {
            TryDelete(databasePath);
        }
    }

    [Test]
    public async Task Skips_v2_subagent_sessions_that_have_a_parent()
    {
        var databasePath = CreateV2Database(
            new Seed("ses_root", "Root run", "ROOT_OK"),
            new Seed("ses_child", "Child run", "CHILD_OK", ParentId: "ses_root"));

        try
        {
            var sessions = await CreateService(databasePath)
                .GetSessionsAsync(includeArchived: false, CancellationToken.None);

            sessions.Select(session => session.Id).Should().BeEquivalentTo(["ses_root"]);
        }
        finally
        {
            TryDelete(databasePath);
        }
    }

    private static OpenCodeSessionService CreateService(string databasePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bridge:OpenCodeDbPath"] = databasePath
            })
            .Build();

        return new OpenCodeSessionService(
            configuration,
            Options.Create(new BridgeOptions()),
            NullLogger<OpenCodeSessionService>.Instance);
    }

    private sealed record Seed(string Id, string Title, string Reply, string? ParentId = null);

    private static string CreateV2Database(params Seed[] sessions)
    {
        var path = Path.Combine(Path.GetTempPath(), $"opencode-v2-{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                create table session_v2 (
                    id text primary key,
                    parent_id text,
                    directory text,
                    title text,
                    time_created integer not null,
                    time_updated integer not null,
                    time_archived integer);

                create table session_message (
                    id text primary key,
                    session_id text not null,
                    type text not null,
                    seq integer,
                    time_created integer not null,
                    time_updated integer not null,
                    data text not null);
                """;
            create.ExecuteNonQuery();
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (id, title, reply, parentId) in sessions)
        {
            using var insertSession = connection.CreateCommand();
            insertSession.CommandText = """
                insert into session_v2 (id, parent_id, directory, title, time_created, time_updated)
                values (@id, @parentId, 'C:/dev/howsitgoing', @title, @now, @now);
                """;
            insertSession.Parameters.AddWithValue("@id", id);
            insertSession.Parameters.AddWithValue("@parentId", (object?)parentId ?? DBNull.Value);
            insertSession.Parameters.AddWithValue("@title", title);
            insertSession.Parameters.AddWithValue("@now", now);
            insertSession.ExecuteNonQuery();

            using var insertMessage = connection.CreateCommand();
            insertMessage.CommandText = """
                insert into session_message (id, session_id, type, seq, time_created, time_updated, data)
                values (@messageId, @sessionId, 'assistant', 1, @now, @now, @data);
                """;
            insertMessage.Parameters.AddWithValue("@messageId", $"msg_{id}");
            insertMessage.Parameters.AddWithValue("@sessionId", id);
            insertMessage.Parameters.AddWithValue("@now", now);
            insertMessage.Parameters.AddWithValue(
                "@data",
                $$"""{"time":{"created":{{now}}},"agent":"build","content":[{"type":"reasoning","text":"thinking"},{"type":"text","text":"{{reply}}"}]}""");
            insertMessage.ExecuteNonQuery();
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
