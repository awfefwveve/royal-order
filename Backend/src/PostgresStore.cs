#if POSTGRES
using System.Text.Json;
using Npgsql;

namespace KnifeWar.Backend;

/// <summary>
/// PostgreSQL 저장소 (상용). 빌드: dotnet publish -c Release -p:UsePostgres=true
/// appsettings.json: "Storage": "postgres", "ConnectionString": "Host=...;Database=knifewar;Username=...;Password=..."
/// 테이블은 처음 실행할 때 자동 생성.
/// </summary>
public sealed class PostgresStore : IStore, IDisposable
{
    readonly NpgsqlDataSource _db;
    static readonly JsonSerializerOptions Json = new();

    public PostgresStore(string connectionString)
    {
        _db = NpgsqlDataSource.Create(connectionString);
        using var cmd = _db.CreateCommand(@"
            create table if not exists accounts (
                id text primary key,
                provider text not null,
                external_id text not null,
                name text not null,
                home_region text not null default '',
                created_at bigint not null,
                last_login_at bigint not null,
                banned boolean not null default false,
                stats jsonb not null default '{}'::jsonb,
                unique (provider, external_id)
            );
            create table if not exists matches (
                id text primary key,
                region text not null,
                server_id text not null,
                created_at bigint not null,
                ended_at bigint not null default 0,
                winner_team int not null default -1,
                players jsonb not null
            );
            create index if not exists matches_created on matches (created_at desc);
            alter table accounts add column if not exists extra jsonb not null default '{}'::jsonb;
            create index if not exists accounts_nametag on accounts (lower(name), (extra ->> 'Tag'));
            create table if not exists reports (
                id text primary key,
                reporter_id text not null,
                target_id text not null,
                match_id text not null,
                reason text not null,
                note text not null default '',
                created_at bigint not null,
                handled boolean not null default false,
                unique (reporter_id, target_id, match_id)
            );
            create index if not exists reports_open on reports (handled, created_at desc);");
        cmd.ExecuteNonQuery();
    }

    static Account ReadAccount(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0), Provider = r.GetString(1), ExternalId = r.GetString(2), Name = r.GetString(3),
        HomeRegion = r.GetString(4), CreatedAt = r.GetInt64(5), LastLoginAt = r.GetInt64(6), Banned = r.GetBoolean(7),
        Stats = JsonSerializer.Deserialize<Dictionary<string, RegionStats>>(r.GetString(8), Json) ?? new(),
        Extra = JsonSerializer.Deserialize<AccountExtra>(r.GetString(9), Json) ?? new(),
    };

    const string AccountCols = "id, provider, external_id, name, home_region, created_at, last_login_at, banned, stats::text, extra::text";

    public Account FindAccount(string provider, string externalId)
    {
        using var cmd = _db.CreateCommand($"select {AccountCols} from accounts where provider = $1 and external_id = $2");
        cmd.Parameters.AddWithValue(provider);
        cmd.Parameters.AddWithValue(externalId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAccount(r) : null;
    }

    public Account GetAccount(string id)
    {
        if (id == null) return null;
        using var cmd = _db.CreateCommand($"select {AccountCols} from accounts where id = $1");
        cmd.Parameters.AddWithValue(id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAccount(r) : null;
    }

    public void SaveAccount(Account a)
    {
        using var cmd = _db.CreateCommand(@"
            insert into accounts (id, provider, external_id, name, home_region, created_at, last_login_at, banned, stats, extra)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9::jsonb, $10::jsonb)
            on conflict (id) do update set name = $4, home_region = $5, last_login_at = $7, banned = $8, stats = $9::jsonb, extra = $10::jsonb");
        cmd.Parameters.AddWithValue(a.Id);
        cmd.Parameters.AddWithValue(a.Provider);
        cmd.Parameters.AddWithValue(a.ExternalId);
        cmd.Parameters.AddWithValue(a.Name);
        cmd.Parameters.AddWithValue(a.HomeRegion ?? "");
        cmd.Parameters.AddWithValue(a.CreatedAt);
        cmd.Parameters.AddWithValue(a.LastLoginAt);
        cmd.Parameters.AddWithValue(a.Banned);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(a.Stats, Json));
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(a.Extra ?? new AccountExtra(), Json));
        cmd.ExecuteNonQuery();
    }

    public bool NameTagTaken(string name, string tag)
    {
        using var cmd = _db.CreateCommand("select 1 from accounts where lower(name) = lower($1) and extra ->> 'Tag' = $2 limit 1");
        cmd.Parameters.AddWithValue(name);
        cmd.Parameters.AddWithValue(tag);
        return cmd.ExecuteScalar() != null;
    }

    public void SaveReport(Report rp)
    {
        using var cmd = _db.CreateCommand(@"
            insert into reports (id, reporter_id, target_id, match_id, reason, note, created_at, handled)
            values ($1, $2, $3, $4, $5, $6, $7, $8)
            on conflict (id) do update set handled = $8");
        cmd.Parameters.AddWithValue(rp.Id);
        cmd.Parameters.AddWithValue(rp.ReporterId);
        cmd.Parameters.AddWithValue(rp.TargetId);
        cmd.Parameters.AddWithValue(rp.MatchId);
        cmd.Parameters.AddWithValue(rp.Reason);
        cmd.Parameters.AddWithValue(rp.Note ?? "");
        cmd.Parameters.AddWithValue(rp.CreatedAt);
        cmd.Parameters.AddWithValue(rp.Handled);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Report> Reports(bool onlyOpen, int count)
    {
        using var cmd = _db.CreateCommand(@"select id, reporter_id, target_id, match_id, reason, note, created_at, handled from reports
            where ($1 = false or handled = false) order by created_at desc limit $2");
        cmd.Parameters.AddWithValue(onlyOpen);
        cmd.Parameters.AddWithValue(count);
        var list = new List<Report>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Report
            {
                Id = r.GetString(0), ReporterId = r.GetString(1), TargetId = r.GetString(2), MatchId = r.GetString(3),
                Reason = r.GetString(4), Note = r.GetString(5), CreatedAt = r.GetInt64(6), Handled = r.GetBoolean(7),
            });
        return list;
    }

    public bool HasReport(string reporterId, string targetId, string matchId)
    {
        using var cmd = _db.CreateCommand("select 1 from reports where reporter_id = $1 and target_id = $2 and match_id = $3");
        cmd.Parameters.AddWithValue(reporterId);
        cmd.Parameters.AddWithValue(targetId);
        cmd.Parameters.AddWithValue(matchId);
        return cmd.ExecuteScalar() != null;
    }

    public void SaveMatch(MatchRecord m)
    {
        using var cmd = _db.CreateCommand(@"
            insert into matches (id, region, server_id, created_at, ended_at, winner_team, players)
            values ($1, $2, $3, $4, $5, $6, $7::jsonb)
            on conflict (id) do update set ended_at = $5, winner_team = $6, players = $7::jsonb");
        cmd.Parameters.AddWithValue(m.Id);
        cmd.Parameters.AddWithValue(m.Region);
        cmd.Parameters.AddWithValue(m.ServerId ?? "");
        cmd.Parameters.AddWithValue(m.CreatedAt);
        cmd.Parameters.AddWithValue(m.EndedAt);
        cmd.Parameters.AddWithValue(m.WinnerTeam);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(m.Players, Json));
        cmd.ExecuteNonQuery();
    }

    public MatchRecord GetMatch(string id)
    {
        using var cmd = _db.CreateCommand("select id, region, server_id, created_at, ended_at, winner_team, players::text from matches where id = $1");
        cmd.Parameters.AddWithValue(id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new MatchRecord
        {
            Id = r.GetString(0), Region = r.GetString(1), ServerId = r.GetString(2), CreatedAt = r.GetInt64(3),
            EndedAt = r.GetInt64(4), WinnerTeam = r.GetInt32(5),
            Players = JsonSerializer.Deserialize<List<MatchPlayer>>(r.GetString(6), Json) ?? new(),
        };
    }

    public IReadOnlyList<Account> TopAccounts(string region, int count)
    {
        using var cmd = _db.CreateCommand($@"
            select {AccountCols} from accounts
            where (stats -> $1 ->> 'Matches')::int > 0
            order by (stats -> $1 ->> 'Mmr')::int desc limit $2");
        cmd.Parameters.AddWithValue(region);
        cmd.Parameters.AddWithValue(count);
        var list = new List<Account>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadAccount(r));
        return list;
    }

    public void Dispose() => _db.Dispose();
}
#endif
