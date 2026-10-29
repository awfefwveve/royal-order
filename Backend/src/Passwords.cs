using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace KnifeWar.Backend;

/// <summary>비밀번호 저장: PBKDF2-SHA256, 계정마다 무작위 소금, 21만 회 반복. 원문은 어디에도 저장하지 않는다.</summary>
public static class Passwords
{
    const int Iterations = 210_000;
    const int SaltBytes = 16, HashBytes = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored) || password == null) return false;
        var p = stored.Split('$');
        if (p.Length != 4 || p[0] != "pbkdf2-sha256" || !int.TryParse(p[1], out int iter)) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(p[2]); expected = Convert.FromBase64String(p[3]); } catch { return false; }
        var got = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(got, expected);
    }

    /// <summary>숫자 6자리 코드 (인증·재설정).</summary>
    public static string Code() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>이름 뒤 태그 (숫자 4자리, 1000~9999).</summary>
    public static string Tag() => RandomNumberGenerator.GetInt32(1000, 10000).ToString();
}

/// <summary>메일 발송. SMTP 설정이 없으면 콘솔에 출력 (개발 중 코드 확인용).</summary>
public sealed class Mailer
{
    readonly SmtpOptions _o;
    readonly Action<string> _log;

    public Mailer(SmtpOptions o, Action<string> log) { _o = o; _log = log; }

    public void Send(string to, string subject, string body)
    {
        if (string.IsNullOrEmpty(_o.Host) || string.IsNullOrEmpty(_o.Password))   // 메일 키가 없으면 개발 모드: 콘솔에만
        {
            _log($"[mail] to={to} subject={subject} :: {body}");
            return;
        }
        // 응답을 늦추지 않게 백그라운드로
        _ = Task.Run(() =>
        {
            try
            {
                using var c = new SmtpClient(_o.Host, _o.Port) { EnableSsl = _o.EnableSsl };
                if (!string.IsNullOrEmpty(_o.User)) c.Credentials = new System.Net.NetworkCredential(_o.User, _o.Password);
                c.Send(new MailMessage(_o.From, to, subject, body));
            }
            catch (Exception e) { _log($"[mail] send failed to={to}: {e.Message}"); }
        });
    }
}
