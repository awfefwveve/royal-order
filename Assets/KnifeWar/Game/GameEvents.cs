namespace KnifeWar.Game
{
    public enum NetRole : byte
    {
        None = 0,
        Offline = 1,   // 혼자 + 봇 (네트워크 없음)
        Host = 2,      // 서버 + 이 컴퓨터의 플레이어 (테스트·친구 방)
        Server = 3,    // 데디 서버 (화면·사람 없음)
        Client = 4,    // 서버에 접속한 플레이어
    }

    public enum GameEventType : byte
    {
        Kill = 1,           // A=victim, B=attacker(-1), C=how
        Hit = 2,            // A=attacker, B=victim, C=damage, D=part, E=kill
        MonsterHit = 3,     // A=attacker, C=damage, F1=x, F2=z
        MonsterHurt = 4,    // A=victim, C=damage, E=kill
        Swing = 5,          // A=player, B=attack type
        Summon = 6,         // A=player, B=card id (카드 사용 성공)
        Field = 8,          // A=team, B=field id, D=end tick (필드 효과 시작 — 화면 연출용)
        Message = 7,        // A=player(받는 사람), B=message code
        MonsterSpawn = 20,  // A=slot, B=team, C=type, D=pos(mm), E=lat(mm), F=state
        MonsterState = 21,  // A=slot, B=state, D=pos
        MonsterDeath = 22,  // A=slot
    }

    public enum KillHow : byte { Slash = 0, Stab = 1, Head = 2, Backstab = 3, Fall = 4, Monster = 5 }

    public enum GameMessage : byte { NoMana = 0, TeamCap = 1, NotKing = 2, SummonFail = 3, BuffOk = 4, BuffFail = 5, KingTaken = 6, Limit = 7 }

    /// <summary>서버에서 일어난 일 하나. 호스트는 바로 처리하고, 클라에는 네트워크로 그대로 보낸다.</summary>
    public struct GameEvent
    {
        public GameEventType Type;
        public int A, B, C, D, E, F;
        public float F1, F2;
    }

    public static class GameText
    {
        public static string How(KillHow h)
        {
            switch (h)
            {
                case KillHow.Slash: return L.T("how_slash");
                case KillHow.Stab: return L.T("how_stab");
                case KillHow.Head: return L.T("how_head");
                case KillHow.Backstab: return L.T("how_backstab");
                case KillHow.Fall: return L.T("how_fall");
                default: return L.T("how_monster");
            }
        }

        public static string Message(GameMessage m)
        {
            switch (m)
            {
                case GameMessage.NoMana: return L.T("msg_no_mana");
                case GameMessage.TeamCap: return L.T("msg_team_cap");
                case GameMessage.NotKing: return L.T("msg_not_king");
                case GameMessage.KingTaken: return L.T("msg_king_taken");
                case GameMessage.BuffOk: return L.T("msg_buff_ok");
                case GameMessage.BuffFail: return L.T("msg_buff_fail");
                case GameMessage.Limit: return L.T("msg_limit");
                default: return L.T("msg_summon_fail");
            }
        }
    }
}
