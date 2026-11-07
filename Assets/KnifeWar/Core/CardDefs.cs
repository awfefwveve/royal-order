using System;

namespace KnifeWar.Core
{
    public enum CardKind : byte
    {
        Summon = 0,     // 몬스터 한 마리를 1층 출구에 소환
        Field = 1,      // 필드 효과 (맵 전체) + 부수 효과
        Building = 2,   // 자기 진영 건물 (자동 생산·마나)
        Buff = 3,       // 아군 기사 버프
    }

    public enum MonsterAttr : byte { None = 0, Fire = 1, Dark = 2 }

    /// <summary>카드 한 장의 정의. 이름·설명 문자열은 Game 쪽 L 사전에서 Key 로 찾는다 (card_&lt;Key&gt;_name / _desc).</summary>
    public struct CardDef
    {
        public byte Id;
        public string Key;
        public CardKind Kind;
        public byte MonsterType;    // Summon: 소환 종류 / Field(볼케이노): 같이 나오는 골렘
        public byte Cost;           // 마나
        public byte Param;          // Building: 건물 종류 / Buff: 버프 id
        public ushort DurationSec;  // Field·Buff 지속 시간
        public byte TeamLimit;      // Building: 팀당 최대 개수, Field: 동시 1
        public MonsterAttr Attr;    // 카드 그림 색 등 표시용
    }

    /// <summary>건물 종류 (CardDef.Param).</summary>
    public static class BuildingType
    {
        public const byte Barracks = 0;   // 12초마다 졸병 1 (건물 수만큼)
        public const byte ManaWell = 1;   // 왕 마나 회복 +1.5/초 (건물 수만큼)
        public const int Count = 2;
        public const int BarracksIntervalSec = 12;
        public const int ManaWellMilliPerSec = 1500;
    }

    /// <summary>필드 효과 id (TeamField).</summary>
    public static class FieldType
    {
        public const int None = -1;
        public const int Volcano = 0;     // 맵이 화산 지형, 화염 골렘 소환, 화염 속성 체력·이동 증가
        public const int FireHpPercent = 50;    // 화염 속성: 소환 시 체력 +50%
        public const int FireSpeedPercent = 25; // 화염 속성: 이동 속도 +25%
    }

    /// <summary>버프 id (TeamBuffs).</summary>
    public static class BuffType
    {
        public const int WarHorn = 1;     // 아군 기사 이동 속도 +15%
        public const float WarHornSpeed = 1.15f;
    }

    /// <summary>
    /// 왕 카드 전체 목록 + 덱 규칙. 수치는 전부 임시 (밸런스는 플레이테스트로).
    /// 덱 8장, 손패 4장, 같은 카드 최대 2장. 쓴 카드는 덱 맨 뒤로 돌아가고 다음 카드를 뽑는다.
    /// 마나: 기본 회복 5.5/초 → 빈 상태에서 오우거(60)를 약 11초마다, 현현(100)은 약 18초마다.
    /// </summary>
    public static class CardDefs
    {
        public const int DeckSize = 8;
        public const int HandSize = 4;
        public const int MaxCopies = 2;
        public const byte None = 255;
        /// <summary>항상 쓸 수 있는 고정 능력 "현현": 마나 100으로 죽은 아군 기사 1명을 성에서 되살린다 (손패 밖, 5번 키).</summary>
        public const byte ResurrectId = 200;
        public const int ResurrectCost = 100;
        public const float ManaPerSecond = 5.5f;

        public static readonly CardDef[] All =
        {
            new CardDef { Id = 0, Key = "footman",   Kind = CardKind.Summon,   MonsterType = 0, Cost = 10 },
            new CardDef { Id = 1, Key = "shield",    Kind = CardKind.Summon,   MonsterType = 1, Cost = 20 },
            new CardDef { Id = 2, Key = "archer",    Kind = CardKind.Summon,   MonsterType = 2, Cost = 15 },
            new CardDef { Id = 3, Key = "ogre",      Kind = CardKind.Summon,   MonsterType = 3, Cost = 60 },
            new CardDef { Id = 4, Key = "twinogre",  Kind = CardKind.Summon,   MonsterType = 4, Cost = 80 },
            new CardDef { Id = 5, Key = "deathknight", Kind = CardKind.Summon, MonsterType = 5, Cost = 70, Attr = MonsterAttr.Dark },
            new CardDef { Id = 6, Key = "volcano",   Kind = CardKind.Field,    MonsterType = 6, Cost = 90, Param = (byte)FieldType.Volcano, DurationSec = 60, TeamLimit = 1, Attr = MonsterAttr.Fire },
            new CardDef { Id = 7, Key = "barracks",  Kind = CardKind.Building, Cost = 45, Param = BuildingType.Barracks, TeamLimit = 3 },
            new CardDef { Id = 8, Key = "manawell",  Kind = CardKind.Building, Cost = 30, Param = BuildingType.ManaWell, TeamLimit = 2 },
            new CardDef { Id = 9, Key = "warhorn",   Kind = CardKind.Buff,     Cost = 25, Param = (byte)BuffType.WarHorn, DurationSec = 8 },
        };

        /// <summary>현현 (카드 목록 밖, 그리기·설명용).</summary>
        public static readonly CardDef Resurrect = new CardDef { Id = ResurrectId, Key = "resurrect", Kind = CardKind.Buff, Cost = ResurrectCost };

        public static int Count => All.Length;

        /// <summary>기본 덱 (덱을 안 만든 계정·봇).</summary>
        public static readonly byte[] DefaultDeck = { 0, 0, 2, 1, 3, 7, 9, 6 };

        public static bool IsValidId(int id) => id >= 0 && id < All.Length;

        public static ref readonly CardDef Get(int id) => ref id == ResurrectId ? ref Resurrect : ref All[id];

        /// <summary>덱이 규칙에 맞나 (8장, 전부 있는 카드, 같은 카드 2장 이하).</summary>
        public static bool IsValidDeck(byte[] deck)
        {
            if (deck == null || deck.Length != DeckSize) return false;
            var copies = new int[All.Length];
            for (int i = 0; i < deck.Length; i++)
            {
                if (!IsValidId(deck[i])) return false;
                if (++copies[deck[i]] > MaxCopies) return false;
            }
            return true;
        }

        /// <summary>유효하면 복사본, 아니면 기본 덱 복사본.</summary>
        public static byte[] Normalize(byte[] deck)
        {
            var src = IsValidDeck(deck) ? deck : DefaultDeck;
            var copy = new byte[DeckSize];
            Array.Copy(src, copy, DeckSize);
            return copy;
        }

        /// <summary>"0,0,2,1,3,7,9,6" ↔ byte[] (저장·네트워크용).</summary>
        public static string ToText(byte[] deck) => deck == null ? "" : string.Join(",", deck);

        public static byte[] Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var parts = text.Split(',');
            var deck = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!byte.TryParse(parts[i].Trim(), out deck[i])) return null;
            }
            return deck;
        }
    }
}
