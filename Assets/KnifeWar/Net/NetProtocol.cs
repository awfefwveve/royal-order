using System;
using KnifeWar.Core;
using KnifeWar.Game;
using UnityEngine;

namespace KnifeWar.Net
{
    /// <summary>메시지 종류. 첫 바이트.</summary>
    public enum Msg : byte
    {
        Hello = 1,      // C→S (신뢰): 프로토콜 버전, 테스트용 지연
        Commands = 2,   // C→S (비신뢰): 최근 입력 최대 4개 (손실 대비 중복 전송)
        Welcome = 10,   // S→C (신뢰): 배정된 칸
        Snapshot = 11,  // S→C (비신뢰, 매 틱): 플레이어·라운드 상태
        Events = 12,    // S→C (신뢰): 이번 틱에 일어난 일
        Full = 13,      // S→C: 방이 가득 참
        Roster = 14,    // S→C (신뢰): 칸별 이름
        FullState = 15, // S→C (신뢰): 입장·재접속 직후 현재 상태 전체 (살아 있는 몬스터, 킬/데스)
        Reject = 16,    // S→C (신뢰): 입장 거부 + 이유 (다음 바이트 RejectReason)
        Bye = 3,        // C→S (신뢰): 스스로 나감 (재접속 대기 없이 바로 봇 전환)
    }

    /// <summary>입장 거부 이유. 클라는 이유에 따라 재접속을 계속할지 정한다.</summary>
    public enum RejectReason : byte
    {
        Version = 1,    // 게임 버전이 다름 → 업데이트 필요 (재시도 안 함)
        NotReady = 2,   // 서버가 아직 경기 배정 전 (잠시 후 재시도)
        BadToken = 3,   // 이 서버의 경기가 아님 = 경기가 이미 끝남 (재시도 안 함)
        Full = 4,       // 자리 없음 (재시도 안 함)
    }

    public static class NetProtocol
    {
        public const ushort Version = 7;   // 7: 왕 카드 (입장 때 덱, 스냅샷에 손패·필드·건물), 몬스터 7종
        public const ushort DefaultPort = 7777;
        public const int MaxCommandsPerPacket = 4;
        public const int CorrectionsPerSnapshot = 16;

        // ---- 입력 ----

        public static void WriteCommand(ByteWriter w, in NetCommand c)
        {
            var i = c.Intent;
            w.UInt(c.Seq);
            w.SByte((sbyte)Mathf.RoundToInt(Mathf.Clamp(i.Move.x, -1f, 1f) * 127f));
            w.SByte((sbyte)Mathf.RoundToInt(Mathf.Clamp(i.Move.y, -1f, 1f) * 127f));
            byte flags = 0;
            if (i.Jump) flags |= 1;
            if (i.Crouch) flags |= 2;
            if (i.Walk) flags |= 4;
            if (i.Buff) flags |= 8;
            w.Byte(flags);
            w.Float(i.Yaw);
            w.Float(i.Pitch);
            w.Byte((byte)i.Attack);
            w.SByte(i.Summon);
            w.SByte(i.PickClass);
            w.SByte(i.Vote);
            w.Int(c.ViewTick);
            w.Byte((byte)Mathf.Clamp(Mathf.RoundToInt(c.ViewAlpha * 255f), 0, 255));
            w.Int(c.MonsterTick);
        }

        public static NetCommand ReadCommand(ByteReader r)
        {
            var c = new NetCommand();
            c.Seq = r.UInt();
            var i = new PlayerIntent();
            i.Move = new Vector2(r.SByte() / 127f, r.SByte() / 127f);
            byte flags = r.Byte();
            i.Jump = (flags & 1) != 0;
            i.Crouch = (flags & 2) != 0;
            i.Walk = (flags & 4) != 0;
            i.Buff = (flags & 8) != 0;
            i.Yaw = r.Float();
            i.Pitch = r.Float();
            i.Attack = (KnifeAttack)r.Byte();
            i.Summon = r.SByte();
            i.PickClass = r.SByte();
            i.Vote = r.SByte();
            c.Intent = i;
            c.ViewTick = r.Int();
            c.ViewAlpha = r.Byte() / 255f;
            c.MonsterTick = r.Int();
            return c;
        }

        // ---- 플레이어 상태 ----

        public static void WritePlayer(ByteWriter w, in PlayerNetState p)
        {
            w.Float(p.Pos.x);
            w.Float(p.Pos.y);
            w.Float(p.Pos.z);
            w.UShort(AngleToU16(p.Yaw));
            w.Short((short)Mathf.RoundToInt(Mathf.Clamp(p.Pitch, -90f, 90f) * 300f));
            w.Byte((byte)Mathf.RoundToInt(Mathf.Clamp01(p.Crouch) * 255f));
            byte flags = 0;
            if (p.Alive) flags |= 1;
            if (p.Class == PlayerClass.King) flags |= 2;
            if (p.Pick == PlayerClass.King) flags |= 4;
            w.Byte(flags);
            w.Byte((byte)Mathf.Clamp(p.Hp, 0, 255));
            w.Byte(p.TeleportSeq);
            w.SByte(p.Vote);
        }

        public static PlayerNetState ReadPlayer(ByteReader r)
        {
            var p = new PlayerNetState();
            p.Pos = new Vector3(r.Float(), r.Float(), r.Float());
            p.Yaw = U16ToAngle(r.UShort());
            p.Pitch = r.Short() / 300f;
            p.Crouch = r.Byte() / 255f;
            byte flags = r.Byte();
            p.Alive = (flags & 1) != 0;
            p.Class = (flags & 2) != 0 ? PlayerClass.King : PlayerClass.Knight;
            p.Pick = (flags & 4) != 0 ? PlayerClass.King : PlayerClass.Knight;
            p.Hp = r.Byte();
            p.TeleportSeq = r.Byte();
            p.Vote = r.SByte();
            return p;
        }

        static ushort AngleToU16(float deg)
        {
            float d = Mathf.Repeat(deg, 360f);
            return (ushort)Mathf.RoundToInt(d / 360f * 65535f);
        }

        static float U16ToAngle(ushort v) => v / 65535f * 360f;

        // ---- 이벤트 ----

        public static void WriteEvent(ByteWriter w, in GameEvent e)
        {
            w.Byte((byte)e.Type);
            switch (e.Type)
            {
                case GameEventType.Kill:
                    w.Byte((byte)e.A); w.SByte((sbyte)e.B); w.Byte((byte)e.C);
                    break;
                case GameEventType.Hit:
                    w.Byte((byte)e.A); w.Byte((byte)e.B); w.UShort((ushort)e.C); w.Byte((byte)e.D); w.Byte((byte)e.E);
                    break;
                case GameEventType.MonsterHit:
                    w.Byte((byte)e.A); w.UShort((ushort)e.C); w.Float(e.F1); w.Float(e.F2);
                    break;
                case GameEventType.MonsterHurt:
                    w.Byte((byte)e.A); w.UShort((ushort)e.C); w.Byte((byte)e.E);
                    break;
                case GameEventType.Swing:
                    w.Byte((byte)e.A); w.Byte((byte)e.B);
                    break;
                case GameEventType.Summon:
                    w.Byte((byte)e.A); w.Byte((byte)e.B);
                    break;
                case GameEventType.Field:
                    w.Byte((byte)e.A); w.SByte((sbyte)e.B); w.Int(e.D);
                    break;
                case GameEventType.Message:
                    w.Byte((byte)e.A); w.Byte((byte)e.B);
                    break;
                case GameEventType.MonsterSpawn:
                    w.UShort((ushort)e.A); w.Byte((byte)e.B); w.Byte((byte)e.C); w.Int(e.D); w.Short((short)e.E); w.Byte((byte)e.F);
                    break;
                case GameEventType.MonsterState:
                    w.UShort((ushort)e.A); w.Byte((byte)e.B); w.Int(e.D); w.Short((short)e.E);
                    break;
                case GameEventType.MonsterDeath:
                    w.UShort((ushort)e.A);
                    break;
            }
        }

        public static GameEvent ReadEvent(ByteReader r)
        {
            var e = new GameEvent { Type = (GameEventType)r.Byte() };
            switch (e.Type)
            {
                case GameEventType.Kill:
                    e.A = r.Byte(); e.B = r.SByte(); e.C = r.Byte();
                    break;
                case GameEventType.Hit:
                    e.A = r.Byte(); e.B = r.Byte(); e.C = r.UShort(); e.D = r.Byte(); e.E = r.Byte();
                    break;
                case GameEventType.MonsterHit:
                    e.A = r.Byte(); e.C = r.UShort(); e.F1 = r.Float(); e.F2 = r.Float();
                    break;
                case GameEventType.MonsterHurt:
                    e.A = r.Byte(); e.C = r.UShort(); e.E = r.Byte();
                    break;
                case GameEventType.Swing:
                    e.A = r.Byte(); e.B = r.Byte();
                    break;
                case GameEventType.Summon:
                    e.A = r.Byte(); e.B = r.Byte();
                    break;
                case GameEventType.Field:
                    e.A = r.Byte(); e.B = r.SByte(); e.D = r.Int();
                    break;
                case GameEventType.Message:
                    e.A = r.Byte(); e.B = r.Byte();
                    break;
                case GameEventType.MonsterSpawn:
                    e.A = r.UShort(); e.B = r.Byte(); e.C = r.Byte(); e.D = r.Int(); e.E = r.Short(); e.F = r.Byte();
                    break;
                case GameEventType.MonsterState:
                    e.A = r.UShort(); e.B = r.Byte(); e.D = r.Int(); e.E = r.Short();
                    break;
                case GameEventType.MonsterDeath:
                    e.A = r.UShort();
                    break;
                default:
                    throw new FormatException($"unknown event {(byte)e.Type}");
            }
            return e;
        }
    }

    /// <summary>바이트 버퍼 쓰기 (리틀 엔디언). 전송 라이브러리와 무관하게 메시지를 만든다.</summary>
    public sealed class ByteWriter
    {
        byte[] _buf = new byte[1024];
        public int Length { get; private set; }
        public byte[] Buffer => _buf;

        public void Clear() => Length = 0;

        void Ensure(int n)
        {
            if (Length + n <= _buf.Length) return;
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
        }

        public void Byte(byte v) { Ensure(1); _buf[Length++] = v; }
        public void SByte(sbyte v) => Byte((byte)v);
        public void UShort(ushort v) { Ensure(2); _buf[Length++] = (byte)v; _buf[Length++] = (byte)(v >> 8); }
        public void Short(short v) => UShort((ushort)v);
        public void UInt(uint v)
        {
            Ensure(4);
            _buf[Length++] = (byte)v; _buf[Length++] = (byte)(v >> 8); _buf[Length++] = (byte)(v >> 16); _buf[Length++] = (byte)(v >> 24);
        }
        public void Int(int v) => UInt((uint)v);
        public void Float(float v) => UInt((uint)BitConverter.SingleToInt32Bits(v));
        public void Str(string s)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s ?? "");
            UShort((ushort)b.Length);
            Ensure(b.Length);
            Array.Copy(b, 0, _buf, Length, b.Length);
            Length += b.Length;
        }

        public byte[] ToArray()
        {
            var a = new byte[Length];
            Array.Copy(_buf, a, Length);
            return a;
        }
    }

    public sealed class ByteReader
    {
        readonly byte[] _buf;
        int _pos;
        readonly int _len;

        public ByteReader(byte[] buf, int len) { _buf = buf; _len = len; }
        public bool HasMore => _pos < _len;

        public byte Byte()
        {
            if (_pos >= _len) throw new FormatException("read past end");
            return _buf[_pos++];
        }
        public sbyte SByte() => (sbyte)Byte();
        public ushort UShort() { int a = Byte(), b = Byte(); return (ushort)(a | (b << 8)); }
        public short Short() => (short)UShort();
        public uint UInt() { uint a = Byte(), b = Byte(), c = Byte(), d = Byte(); return a | (b << 8) | (c << 16) | (d << 24); }
        public int Int() => (int)UInt();
        public float Float() => BitConverter.Int32BitsToSingle((int)UInt());
        public string Str()
        {
            int n = UShort();
            if (_pos + n > _len) throw new FormatException("string past end");
            string s = System.Text.Encoding.UTF8.GetString(_buf, _pos, n);
            _pos += n;
            return s;
        }
    }
}
