namespace KnifeWar.Backend;

public sealed class KnifeWarOptions
{
    public string DataDir { get; set; } = "data";
    public string Storage { get; set; } = "file";          // file | postgres
    public string ConnectionString { get; set; } = "";
    public string TokenSecret { get; set; } = "";
    public string ServerSecret { get; set; } = "";
    public bool DevAuth { get; set; } = true;
    public string AdminKey { get; set; } = "";                // 운영 도구용 (비우면 운영 API 꺼짐). 배포는 KW_ADMIN_KEY
    public bool RequireEmailVerification { get; set; }        // 켜면 이메일 인증 전에는 매칭 불가
    public SmtpOptions Smtp { get; set; } = new();
    public ClientOptions Client { get; set; } = new();
    public SteamOptions Steam { get; set; } = new();
    public AstrelAccountOptions Astrel { get; set; } = new();
    public MatchmakingOptions Matchmaking { get; set; } = new();
    public List<RegionOptions> Regions { get; set; } = new();
}

/// <summary>
/// 게임 자동 업데이트. Version이 비어 있으면 업데이트 확인 안 함.
/// PatchBaseUrl/{Version}/manifest.json 과 PatchBaseUrl/{Version}/files/... 를 CDN(S3·R2 등)에 올려둔다.
/// </summary>
public sealed class ClientOptions
{
    public string Version { get; set; } = "";
    public string PatchBaseUrl { get; set; } = "";
    public string Notice { get; set; } = "";      // 메뉴에 띄울 공지 (점검 안내 등)
    public bool Maintenance { get; set; }          // 점검 중: 로그인·매칭 막음
    public string PatchDir { get; set; } = "";     // 개발용: 이 폴더를 /patch/ 로 직접 제공 (예: ../../Builds/Patch)
    // 약관·개인정보처리방침: 내용을 바꾸면 버전을 올린다 → 다음 로그인 때 모두 다시 동의
    public string TermsVersion { get; set; } = "2026-11";
    public string TermsUrl { get; set; } = "https://astrelgames.com/terms";
    public string PrivacyUrl { get; set; } = "https://astrelgames.com/privacy";
}

/// <summary>인증 코드·비밀번호 재설정 메일 발송. Host가 비면 콘솔에만 출력 (개발용).</summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
}

/// <summary>
/// ASTREL 계정 = 회사 홈페이지 로그인(Firebase). 홈페이지·런처·게임이 같은 계정을 쓴다.
/// ProjectId 가 비면 꺼짐. 홈페이지 주소는 CORS 로 허용.
/// </summary>
public sealed class AstrelAccountOptions
{
    public string FirebaseProjectId { get; set; } = "gokgwaengi";
    public List<string> WebOrigins { get; set; } = new() { "https://astrelgames.com", "https://www.astrelgames.com" };
}

public sealed class SteamOptions
{
    public bool Enabled { get; set; }
    public uint AppId { get; set; }
    public string WebApiKey { get; set; } = "";
}

public sealed class MatchmakingOptions
{
    public int PlayersPerMatch { get; set; } = 12;
    public int MinHumansToStart { get; set; } = 2;
    public int FillWithBotsAfterSeconds { get; set; } = 60;
    public int MmrWindowStart { get; set; } = 100;
    public int MmrWindowGrowPerSecond { get; set; } = 25;
    public int MaxPingMs { get; set; } = 150;
    public int PingWidenAfterSeconds { get; set; } = 30;
}

public sealed class RegionOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Continent { get; set; } = "";
    public string PingUrl { get; set; } = "";
}
