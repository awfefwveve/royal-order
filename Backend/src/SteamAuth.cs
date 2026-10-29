using System.Text.Json;

namespace KnifeWar.Backend;

/// <summary>
/// 스팀 로그인: 클라가 Steamworks GetAuthTicketForWebApi("knifewar")로 받은 티켓을
/// Steam Web API(ISteamUserAuth/AuthenticateUserTicket)로 검증해서 steamid를 얻는다.
/// 스팀 앱 등록(AppId)과 퍼블리셔 Web API 키가 생기면 appsettings.json에서 켠다.
/// </summary>
public sealed class SteamAuth
{
    public const string Identity = "knifewar";
    readonly SteamOptions _opt;
    readonly HttpClient _http;

    public SteamAuth(SteamOptions opt, HttpClient http)
    {
        _opt = opt;
        _http = http;
    }

    public bool Enabled => _opt.Enabled && _opt.AppId != 0 && !string.IsNullOrEmpty(_opt.WebApiKey);

    /// <returns>steamid64, 실패 시 null</returns>
    public async Task<string> VerifyAsync(string ticketHex, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(ticketHex)) return null;
        string url = "https://partner.steam-api.com/ISteamUserAuth/AuthenticateUserTicket/v1/"
                     + $"?key={Uri.EscapeDataString(_opt.WebApiKey)}&appid={_opt.AppId}"
                     + $"&ticket={Uri.EscapeDataString(ticketHex)}&identity={Identity}";
        using var res = await _http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("response", out var r)) return null;
        if (!r.TryGetProperty("params", out var p)) return null;
        if (p.TryGetProperty("result", out var result) && result.GetString() != "OK") return null;
        if (p.TryGetProperty("vacbanned", out var vac) && vac.GetBoolean()) return null;
        return p.TryGetProperty("steamid", out var sid) ? sid.GetString() : null;
    }
}
