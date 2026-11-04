using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace KnifeWar.Core
{
    public enum MonsterState : byte
    {
        Move = 0,
        Attack = 1,
    }

    /// <summary>틱마다 집계되는 카운터 인덱스.</summary>
    public static class SimCounter
    {
        public const int SpawnEvents = 0;
        public const int HpEvents = 1;
        public const int DeathEvents = 2;
        public const int StateEvents = 3;
        public const int DeathsTeam0 = 4;
        public const int DeathsTeam1 = 5;
        public const int LeaksTeam0 = 6;   // 팀0 몬스터가 적 진영 끝까지 도달
        public const int LeaksTeam1 = 7;
        public const int Count = 8;
    }

    /// <summary>
    /// 서버 권위 몬스터 시뮬레이션.
    /// - 몬스터마다 오브젝트를 만들지 않고, 고정 용량 슬롯 + 구조체 배열(SoA)로 관리한다.
    /// - 위치는 "다리 위 거리(mm) + 좌우 오프셋(mm)" 정수 좌표.
    /// - Jobs + Burst로 계산하고, 넷코드(Fusion)에 전혀 의존하지 않는다.
    ///
    /// 사용 순서 (매 틱): PrepareTick() → TrySpawn(...) → Step() → QueryMelee(...)
    /// </summary>
    public sealed class MonsterSim : IDisposable
    {
        public readonly int Capacity;
        public readonly int TickRate;
        public readonly int HistoryLength;
        public readonly LaneConfig Lane;

        public NativeArray<MonsterDef> Defs;

        // ---- 슬롯별 상태 (SoA) ----
        public NativeArray<byte> Alive;
        public NativeArray<byte> Team;          // 0: +방향 진행, 1: -방향 진행
        public NativeArray<byte> Type;
        public NativeArray<byte> State;         // MonsterState
        public NativeArray<byte> StateChanged;  // 이번 틱 상태 변화 (네트워크 이벤트 집계용)
        public NativeArray<byte> Leak;          // 이번 틱 적 진영 도달
        public NativeArray<int> Pos;            // 다리 위 거리 (mm)
        public NativeArray<short> Lat;          // 좌우 오프셋 (mm)
        public NativeArray<int> Hp;
        public NativeArray<int> Cooldown;       // 다음 공격까지 남은 틱
        public NativeArray<int> Target;         // 공격 대상 슬롯, 없으면 -1
        public NativeArray<int> SpawnTick;

        /// <summary>랙 보정용 위치 기록. [ (tick % HistoryLength) * Capacity + slot ] = (pos, lat)</summary>
        public NativeArray<int2> History;

        public NativeArray<int> Counters;

        // ---- 플레이어 (몬스터가 공격할 대상) ----
        public const int MaxSimPlayers = 16;
        public const int PlayerRadiusMm = 350;
        public NativeArray<int> PlayerPos;      // 레인 좌표 (mm)
        public NativeArray<int> PlayerLat;
        public NativeArray<byte> PlayerTeam;
        public NativeArray<byte> PlayerAlive;
        public NativeArray<byte> PlayerKing;     // 왕이면 1 → 몬스터는 기본적으로 적 왕을 향해 간다
        public NativeArray<int> PlayerDamage;   // 이번 틱에 몬스터에게 받은 데미지 (PrepareTick에서 0으로)
        public NativeArray<int> TargetPlayer;   // 몬스터별 공격 중인 플레이어, 없으면 -1
        public int PlayerCount;

        // ---- 공간 분할 (팀별) ----
        public NativeArray<int> CellStart;  // [team * (CellCount + 1) + cell]
        public NativeArray<int> CellItems;  // [team * Capacity + i]
        NativeArray<int> _cellCursor;

        NativeList<int> _free;
        readonly int[] _aliveTeam = new int[2];
        int _spawnSerial;
        JobHandle _handle;
        bool _scheduled;

        public int Tick { get; private set; } = -1;
        public int AliveCount => _aliveTeam[0] + _aliveTeam[1];
        public int AliveInTeam(int team) => _aliveTeam[team];
        public int FreeSlots => _free.Length;

        public MonsterSim(int capacity, int tickRate, LaneConfig lane, int maxRewindMs = 250)
        {
            Capacity = capacity;
            TickRate = tickRate;
            Lane = lane;
            HistoryLength = (int)math.ceil(maxRewindMs * tickRate / 1000f) + 2;

            var p = Allocator.Persistent;
            Defs = MonsterDefs.Create(tickRate, p);

            Alive = new NativeArray<byte>(capacity, p);
            Team = new NativeArray<byte>(capacity, p);
            Type = new NativeArray<byte>(capacity, p);
            State = new NativeArray<byte>(capacity, p);
            StateChanged = new NativeArray<byte>(capacity, p);
            Leak = new NativeArray<byte>(capacity, p);
            Pos = new NativeArray<int>(capacity, p);
            Lat = new NativeArray<short>(capacity, p);
            Hp = new NativeArray<int>(capacity, p);
            Cooldown = new NativeArray<int>(capacity, p);
            Target = new NativeArray<int>(capacity, p);
            SpawnTick = new NativeArray<int>(capacity, p);
            History = new NativeArray<int2>(capacity * HistoryLength, p);
            Counters = new NativeArray<int>(SimCounter.Count, p);
            PlayerPos = new NativeArray<int>(MaxSimPlayers, p);
            PlayerLat = new NativeArray<int>(MaxSimPlayers, p);
            PlayerTeam = new NativeArray<byte>(MaxSimPlayers, p);
            PlayerAlive = new NativeArray<byte>(MaxSimPlayers, p);
            PlayerKing = new NativeArray<byte>(MaxSimPlayers, p);
            PlayerDamage = new NativeArray<int>(MaxSimPlayers, p);
            TargetPlayer = new NativeArray<int>(capacity, p);

            int stride = lane.CellCount + 1;
            CellStart = new NativeArray<int>(2 * stride, p);
            _cellCursor = new NativeArray<int>(2 * stride, p);
            CellItems = new NativeArray<int>(2 * capacity, p);

            _free = new NativeList<int>(capacity, p);
            for (int i = capacity - 1; i >= 0; i--) _free.Add(i);
            for (int i = 0; i < capacity; i++) { Target[i] = -1; TargetPlayer[i] = -1; }
        }

        /// <summary>새 틱 시작. 카운터를 비우고 틱 번호를 올린다.</summary>
        public void PrepareTick()
        {
            Complete();
            Tick++;
            for (int i = 0; i < SimCounter.Count; i++) Counters[i] = 0;
            for (int i = 0; i < MaxSimPlayers; i++) PlayerDamage[i] = 0;
        }

        /// <summary>플레이어 위치 갱신 (매 틱 Step 전에). 레인 좌표 mm.</summary>
        public void SetPlayer(int index, int posMm, int latMm, byte team, bool alive, bool king = false)
        {
            Complete();
            PlayerKing[index] = (byte)(king ? 1 : 0);
            PlayerPos[index] = posMm;
            PlayerLat[index] = latMm;
            PlayerTeam[index] = team;
            PlayerAlive[index] = (byte)(alive ? 1 : 0);
            if (index >= PlayerCount) PlayerCount = index + 1;
        }

        /// <summary>자기 진영 끝에서 몬스터 소환. 슬롯이 없으면 false.</summary>
        public bool TrySpawn(byte team, byte type)
        {
            Complete();
            if (_free.Length == 0) return false;

            int s = _free[_free.Length - 1];
            _free.RemoveAt(_free.Length - 1);

            uint h = math.hash(new int2(_spawnSerial++, 7919));
            int width = 2 * Lane.HalfWidthMm + 1;

            Alive[s] = 1;
            Team[s] = team;
            Type[s] = type;
            State[s] = (byte)MonsterState.Move;
            StateChanged[s] = 0;
            Leak[s] = 0;
            Pos[s] = team == 0 ? 0 : Lane.LengthMm;
            Lat[s] = (short)((int)(h % (uint)width) - Lane.HalfWidthMm);
            Hp[s] = Defs[type].MaxHp;
            Cooldown[s] = 0;
            Target[s] = -1;
            TargetPlayer[s] = -1;
            SpawnTick[s] = Tick;

            _aliveTeam[team]++;
            Counters[SimCounter.SpawnEvents]++;
            return true;
        }

        /// <summary>모든 몬스터 제거 (라운드 시작 시).</summary>
        public void Clear()
        {
            Complete();
            _free.Clear();
            for (int i = Capacity - 1; i >= 0; i--)
            {
                Alive[i] = 0;
                Target[i] = -1;
                TargetPlayer[i] = -1;
                _free.Add(i);
            }
            _aliveTeam[0] = 0;
            _aliveTeam[1] = 0;
        }

        // ================================================================
        // 퍼펫 모드 (클라이언트): 서버 이벤트로만 상태가 바뀌고, 이동만 직접 진행한다.
        // ================================================================

        public void PuppetSpawn(int slot, byte team, byte type, int pos, short lat, byte state)
        {
            Complete();
            if (slot < 0 || slot >= Capacity) return;
            if (Alive[slot] != 0) _aliveTeam[Team[slot]]--;
            Alive[slot] = 1;
            Team[slot] = team;
            Type[slot] = type;
            Pos[slot] = pos;
            Lat[slot] = lat;
            State[slot] = state;
            Hp[slot] = Defs[type].MaxHp;
            _aliveTeam[team]++;
        }

        public void PuppetSetState(int slot, byte state, int pos, int lat = int.MinValue)
        {
            Complete();
            if (slot < 0 || slot >= Capacity || Alive[slot] == 0) return;
            State[slot] = state;
            Pos[slot] = pos;
            if (lat != int.MinValue) Lat[slot] = (short)math.clamp(lat, -Lane.HalfWidthMm, Lane.HalfWidthMm);
        }

        public void PuppetCorrect(int slot, int pos, byte state)
        {
            Complete();
            if (slot < 0 || slot >= Capacity || Alive[slot] == 0) return;
            Pos[slot] = pos;
            State[slot] = state;
        }

        public void PuppetRemove(int slot)
        {
            Complete();
            if (slot < 0 || slot >= Capacity || Alive[slot] == 0) return;
            Alive[slot] = 0;
            _aliveTeam[Team[slot]]--;
        }

        /// <summary>이동 상태인 몬스터를 한 틱 전진 (서버와 같은 속도 규칙).</summary>
        public void PuppetStep()
        {
            Complete();
            for (int m = 0; m < Capacity; m++)
            {
                if (Alive[m] == 0 || State[m] != (byte)MonsterState.Move) continue;
                Goal(Team[m], Lane, PlayerCount, PlayerPos, PlayerLat, PlayerTeam, PlayerAlive, PlayerKing, out int goal, out int goalLat, out bool king);
                int pos = Pos[m], lat = Lat[m];
                Advance(ref pos, ref lat, goal, goalLat, king, Defs[Type[m]].SpeedMmPerTick, Lane);
                Pos[m] = pos;
                Lat[m] = (short)lat;
            }
        }

        /// <summary>
        /// 몬스터의 목적지: 살아 있는 적 왕이 있으면 그 위치, 없으면 적 진영 끝.
        /// (가는 길에 적 몬스터·플레이어가 사거리에 들어오면 멈춰서 먼저 처리 — AcquireTargetJob)
        /// </summary>
        public static void Goal(int team, LaneConfig lane, int playerCount, NativeArray<int> pPos, NativeArray<int> pLat,
            NativeArray<byte> pTeam, NativeArray<byte> pAlive, NativeArray<byte> pKing, out int goal, out int goalLat, out bool king)
        {
            int enemy = 1 - team;
            for (int p = 0; p < playerCount; p++)
                if (pKing[p] != 0 && pAlive[p] != 0 && pTeam[p] == enemy)
                {
                    goal = math.clamp(pPos[p], 0, lane.LengthMm);
                    goalLat = math.clamp(pLat[p], -lane.HalfWidthMm, lane.HalfWidthMm);
                    king = true;
                    return;
                }
            goal = team == 0 ? lane.LengthMm : 0;
            goalLat = 0;
            king = false;
        }

        /// <summary>한 틱 이동: 목적지 쪽으로 (넘어가지 않게), 왕이 가까우면 좌우도 왕 쪽으로 붙는다.</summary>
        public static void Advance(ref int pos, ref int lat, int goal, int goalLat, bool king, int speed, LaneConfig lane)
        {
            int d = goal - pos;
            pos = math.clamp(pos + math.clamp(d, -speed, speed), 0, lane.LengthMm);
            if (king && math.abs(d) < 12_000)
                lat = math.clamp(lat + math.clamp(goalLat - lat, -speed / 2, speed / 2), -lane.HalfWidthMm, lane.HalfWidthMm);
        }

        /// <summary>앞쪽 방향 (+1 / -1): 목적지 쪽. 같은 위치면 원래 진행 방향.</summary>
        public static int Heading(int team, int pos, int goal) => goal > pos ? 1 : goal < pos ? -1 : (team == 0 ? 1 : -1);

        /// <summary>외부 데미지 (기사 칼 등). 다음 Step에서 체력 0 이하면 제거된다.</summary>
        public void ApplyDamage(int slot, int damage)
        {
            Complete();
            if (slot < 0 || slot >= Capacity || Alive[slot] == 0) return;
            Hp[slot] = Hp[slot] - damage;
        }

        /// <summary>한 틱 시뮬레이션 (스케줄 + 완료).</summary>
        public void Step()
        {
            Schedule();
            Complete();
        }

        public JobHandle Schedule(JobHandle deps = default)
        {
            Complete();

            var grid = new BuildGridJob
            {
                Lane = Lane,
                Capacity = Capacity,
                Alive = Alive,
                Team = Team,
                Pos = Pos,
                CellStart = CellStart,
                Cursor = _cellCursor,
                CellItems = CellItems,
            }.Schedule(deps);

            var acquire = new AcquireTargetJob
            {
                Lane = Lane,
                Capacity = Capacity,
                Defs = Defs,
                Alive = Alive,
                Team = Team,
                Type = Type,
                Pos = Pos,
                Lat = Lat,
                Hp = Hp,
                CellStart = CellStart,
                CellItems = CellItems,
                Target = Target,
                State = State,
                StateChanged = StateChanged,
                PlayerCount = PlayerCount,
                PlayerPos = PlayerPos,
                PlayerLat = PlayerLat,
                PlayerTeam = PlayerTeam,
                PlayerAlive = PlayerAlive,
                PlayerKing = PlayerKing,
                TargetPlayer = TargetPlayer,
            }.Schedule(Capacity, 256, grid);

            var move = new MoveJob
            {
                Lane = Lane,
                Defs = Defs,
                Alive = Alive,
                Team = Team,
                Type = Type,
                Target = Target,
                TargetPlayer = TargetPlayer,
                Pos = Pos,
                Lat = Lat,
                Cooldown = Cooldown,
                PlayerCount = PlayerCount,
                PlayerPos = PlayerPos,
                PlayerLat = PlayerLat,
                PlayerTeam = PlayerTeam,
                PlayerAlive = PlayerAlive,
                PlayerKing = PlayerKing,
            }.Schedule(Capacity, 256, acquire);

            var damage = new DamageJob
            {
                Capacity = Capacity,
                Defs = Defs,
                Alive = Alive,
                Type = Type,
                Target = Target,
                TargetPlayer = TargetPlayer,
                StateChanged = StateChanged,
                Hp = Hp,
                Cooldown = Cooldown,
                Counters = Counters,
                PlayerDamage = PlayerDamage,
            }.Schedule(move);

            var death = new DeathJob
            {
                Capacity = Capacity,
                Team = Team,
                Hp = Hp,
                Alive = Alive,
                Leak = Leak,
                Counters = Counters,
                Free = _free,
            }.Schedule(damage);

            int hist = (Tick % HistoryLength) * Capacity;
            var history = new HistoryJob
            {
                Pos = Pos,
                Lat = Lat,
                Out = History.GetSubArray(hist, Capacity),
            }.Schedule(Capacity, 512, death);

            _handle = history;
            _scheduled = true;
            return _handle;
        }

        public void Complete()
        {
            if (!_scheduled) return;
            _handle.Complete();
            _scheduled = false;
            _aliveTeam[0] -= Counters[SimCounter.DeathsTeam0];
            _aliveTeam[1] -= Counters[SimCounter.DeathsTeam1];
        }

        /// <summary>
        /// 기사 칼 판정 (몬스터 대상, 랙 보정).
        /// 각 쿼리의 RewindTick 시점 위치로 되감아서, 칼 스윕(선분 + 반지름)에 처음 닿는 적 몬스터를 찾는다.
        /// results[i] = 맞은 몬스터 슬롯, 없으면 -1.
        /// </summary>
        public void QueryMelee(NativeArray<MeleeQuery> queries, NativeArray<int> results)
        {
            Complete();
            new MeleeQueryJob
            {
                Lane = Lane,
                Capacity = Capacity,
                Tick = Tick,
                HistoryLength = HistoryLength,
                Defs = Defs,
                Type = Type,
                SpawnTick = SpawnTick,
                History = History,
                CellStart = CellStart,
                CellItems = CellItems,
                Queries = queries,
                Results = results,
            }.Run();
        }

        public void Dispose()
        {
            _handle.Complete();
            _scheduled = false;
            if (Defs.IsCreated) Defs.Dispose();
            if (Alive.IsCreated) Alive.Dispose();
            if (Team.IsCreated) Team.Dispose();
            if (Type.IsCreated) Type.Dispose();
            if (State.IsCreated) State.Dispose();
            if (StateChanged.IsCreated) StateChanged.Dispose();
            if (Leak.IsCreated) Leak.Dispose();
            if (Pos.IsCreated) Pos.Dispose();
            if (Lat.IsCreated) Lat.Dispose();
            if (Hp.IsCreated) Hp.Dispose();
            if (Cooldown.IsCreated) Cooldown.Dispose();
            if (Target.IsCreated) Target.Dispose();
            if (SpawnTick.IsCreated) SpawnTick.Dispose();
            if (History.IsCreated) History.Dispose();
            if (Counters.IsCreated) Counters.Dispose();
            if (PlayerPos.IsCreated) PlayerPos.Dispose();
            if (PlayerLat.IsCreated) PlayerLat.Dispose();
            if (PlayerTeam.IsCreated) PlayerTeam.Dispose();
            if (PlayerAlive.IsCreated) PlayerAlive.Dispose();
            if (PlayerKing.IsCreated) PlayerKing.Dispose();
            if (PlayerDamage.IsCreated) PlayerDamage.Dispose();
            if (TargetPlayer.IsCreated) TargetPlayer.Dispose();
            if (CellStart.IsCreated) CellStart.Dispose();
            if (_cellCursor.IsCreated) _cellCursor.Dispose();
            if (CellItems.IsCreated) CellItems.Dispose();
            if (_free.IsCreated) _free.Dispose();
        }
    }

    /// <summary>기사 칼 한 번의 판정 요청. 좌표는 레인 좌표 (x = 다리 위 거리 mm, y = 좌우 mm).</summary>
    public struct MeleeQuery
    {
        public float2 Origin;
        public float2 Dir;
        public float RangeMm;
        public float RadiusMm;
        public int RewindTick;
        public byte AttackerTeam;
    }

    // =====================================================================
    // Jobs
    // =====================================================================

    /// <summary>팀별로 몬스터를 칸에 정렬 (카운팅 정렬, 슬롯 순서 유지 → 결정적).</summary>
    [BurstCompile(CompileSynchronously = true)]
    struct BuildGridJob : IJob
    {
        public LaneConfig Lane;
        public int Capacity;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<byte> Team;
        [ReadOnly] public NativeArray<int> Pos;
        public NativeArray<int> CellStart;
        public NativeArray<int> Cursor;
        public NativeArray<int> CellItems;

        public void Execute()
        {
            int cellCount = Lane.CellCount;
            int stride = cellCount + 1;

            for (int i = 0; i < CellStart.Length; i++) CellStart[i] = 0;

            for (int m = 0; m < Capacity; m++)
            {
                if (Alive[m] == 0) continue;
                int k = Team[m] * stride + Lane.CellOf(Pos[m]) + 1;
                CellStart[k] = CellStart[k] + 1;
            }

            for (int t = 0; t < 2; t++)
            {
                int b = t * stride;
                for (int c = 0; c < cellCount; c++)
                    CellStart[b + c + 1] = CellStart[b + c + 1] + CellStart[b + c];
            }

            for (int i = 0; i < CellStart.Length; i++) Cursor[i] = CellStart[i];

            for (int m = 0; m < Capacity; m++)
            {
                if (Alive[m] == 0) continue;
                int t = Team[m];
                int k = t * stride + Lane.CellOf(Pos[m]);
                int idx = Cursor[k];
                Cursor[k] = idx + 1;
                CellItems[t * Capacity + idx] = m;
            }
        }
    }

    /// <summary>앞쪽에서 사거리 안의 가장 가까운 적 몬스터를 찾는다 (몬스터별 병렬).</summary>
    [BurstCompile(CompileSynchronously = true)]
    struct AcquireTargetJob : IJobParallelFor
    {
        public LaneConfig Lane;
        public int Capacity;
        [ReadOnly] public NativeArray<MonsterDef> Defs;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<byte> Team;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<int> Pos;
        [ReadOnly] public NativeArray<short> Lat;
        [ReadOnly] public NativeArray<int> Hp;
        [ReadOnly] public NativeArray<int> CellStart;
        [ReadOnly] public NativeArray<int> CellItems;
        public NativeArray<int> Target;
        public NativeArray<byte> State;
        public NativeArray<byte> StateChanged;
        public int PlayerCount;
        [ReadOnly] public NativeArray<int> PlayerPos;
        [ReadOnly] public NativeArray<int> PlayerLat;
        [ReadOnly] public NativeArray<byte> PlayerTeam;
        [ReadOnly] public NativeArray<byte> PlayerAlive;
        [ReadOnly] public NativeArray<byte> PlayerKing;
        public NativeArray<int> TargetPlayer;

        public void Execute(int m)
        {
            if (Alive[m] == 0)
            {
                Target[m] = -1;
                TargetPlayer[m] = -1;
                StateChanged[m] = 0;
                return;
            }

            int team = Team[m];
            int enemy = 1 - team;
            var def = Defs[Type[m]];
            MonsterSim.Goal(team, Lane, PlayerCount, PlayerPos, PlayerLat, PlayerTeam, PlayerAlive, PlayerKing, out int goal, out _, out _);
            int dir = MonsterSim.Heading(team, Pos[m], goal);

            int myPos = Pos[m];
            int myLat = Lat[m];
            int reach = def.AttackRangeMm + def.RadiusMm + MonsterDefs.MaxRadiusMm;
            int cellsAhead = reach / Lane.CellSizeMm + 1;
            int myCell = Lane.CellOf(myPos);
            int cellCount = Lane.CellCount;
            int stride = cellCount + 1;

            int best = -1;
            long bestD2 = long.MaxValue;

            for (int k = -1; k <= cellsAhead; k++)
            {
                int c = myCell + dir * k;
                if (c < 0 || c >= cellCount) continue;

                int s = CellStart[enemy * stride + c];
                int e = CellStart[enemy * stride + c + 1];
                for (int i = s; i < e; i++)
                {
                    int o = CellItems[enemy * Capacity + i];
                    if (Hp[o] <= 0) continue;

                    long along = (long)(Pos[o] - myPos) * dir;
                    if (along < -def.RadiusMm) continue; // 뒤쪽은 무시

                    long dx = Pos[o] - myPos;
                    long dy = Lat[o] - myLat;
                    long d2 = dx * dx + dy * dy;
                    long r = def.AttackRangeMm + def.RadiusMm + Defs[Type[o]].RadiusMm;
                    if (d2 <= r * r && d2 < bestD2)
                    {
                        bestD2 = d2;
                        best = o;
                    }
                }
            }

            Target[m] = best;

            // 적 몬스터가 사거리에 없으면 적 플레이어를 노린다 (멈춰서 공격)
            int bestPlayer = -1;
            if (best < 0)
            {
                long bestPd = long.MaxValue;
                for (int p = 0; p < PlayerCount; p++)
                {
                    if (PlayerAlive[p] == 0 || PlayerTeam[p] != enemy) continue;
                    long along = (long)(PlayerPos[p] - myPos) * dir;
                    if (along < -def.RadiusMm) continue;
                    long dx = PlayerPos[p] - myPos;
                    // 좌우 거리는 절반만 반영: 다리 폭(4m) 어디에 있든 앞을 지나가는 플레이어에게 반응
                    long dy = (PlayerLat[p] - myLat) / 2;
                    long d2 = dx * dx + dy * dy;
                    long r = def.AttackRangeMm + def.RadiusMm + MonsterSim.PlayerRadiusMm;
                    if (d2 <= r * r && d2 < bestPd) { bestPd = d2; bestPlayer = p; }
                }
            }
            TargetPlayer[m] = bestPlayer;

            byte next = best >= 0 || bestPlayer >= 0 ? (byte)MonsterState.Attack : (byte)MonsterState.Move;
            StateChanged[m] = next != State[m] ? (byte)1 : (byte)0;
            State[m] = next;
        }
    }

    /// <summary>대상이 없으면 전진, 있으면 공격 쿨다운 감소 (몬스터별 병렬).</summary>
    [BurstCompile(CompileSynchronously = true)]
    struct MoveJob : IJobParallelFor
    {
        public LaneConfig Lane;
        [ReadOnly] public NativeArray<MonsterDef> Defs;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<byte> Team;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<int> Target;
        [ReadOnly] public NativeArray<int> TargetPlayer;
        public NativeArray<int> Pos;
        public NativeArray<short> Lat;
        public NativeArray<int> Cooldown;
        public int PlayerCount;
        [ReadOnly] public NativeArray<int> PlayerPos;
        [ReadOnly] public NativeArray<int> PlayerLat;
        [ReadOnly] public NativeArray<byte> PlayerTeam;
        [ReadOnly] public NativeArray<byte> PlayerAlive;
        [ReadOnly] public NativeArray<byte> PlayerKing;

        public void Execute(int m)
        {
            if (Alive[m] == 0) return;

            if (Target[m] >= 0 || TargetPlayer[m] >= 0)
            {
                if (Cooldown[m] > 0) Cooldown[m] = Cooldown[m] - 1;
                return;
            }

            // 막는 게 없으면 적 왕에게 (왕이 없으면 적 진영 끝까지 가서 거기서 버팀 — 사라지지 않음)
            Cooldown[m] = 0;
            MonsterSim.Goal(Team[m], Lane, PlayerCount, PlayerPos, PlayerLat, PlayerTeam, PlayerAlive, PlayerKing, out int goal, out int goalLat, out bool king);
            int pos = Pos[m], lat = Lat[m];
            MonsterSim.Advance(ref pos, ref lat, goal, goalLat, king, Defs[Type[m]].SpeedMmPerTick, Lane);
            Pos[m] = pos;
            Lat[m] = (short)lat;
        }
    }

    /// <summary>
    /// 데미지 적용. 여러 몬스터가 한 대상을 때리므로 순차 처리 (슬롯 순서 → 결정적).
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    struct DamageJob : IJob
    {
        public int Capacity;
        [ReadOnly] public NativeArray<MonsterDef> Defs;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<int> Target;
        [ReadOnly] public NativeArray<int> TargetPlayer;
        [ReadOnly] public NativeArray<byte> StateChanged;
        public NativeArray<int> Hp;
        public NativeArray<int> Cooldown;
        public NativeArray<int> Counters;
        public NativeArray<int> PlayerDamage;

        public void Execute()
        {
            int hpEvents = 0;
            int stateEvents = 0;

            for (int m = 0; m < Capacity; m++)
            {
                if (Alive[m] == 0) continue;
                if (StateChanged[m] != 0) stateEvents++;

                int t = Target[m];
                int tp = TargetPlayer[m];
                if ((t < 0 && tp < 0) || Cooldown[m] != 0) continue;
                if (Hp[m] <= 0) continue;                 // 이번 틱에 이미 죽은 몬스터는 공격 못 함

                var def = Defs[Type[m]];
                if (t < 0)
                {
                    PlayerDamage[tp] = PlayerDamage[tp] + def.AttackDamage;
                    Cooldown[m] = def.AttackIntervalTicks;
                    hpEvents++;
                    continue;
                }
                if (Alive[t] == 0 || Hp[t] <= 0) continue;

                Hp[t] = Hp[t] - def.AttackDamage;
                Cooldown[m] = def.AttackIntervalTicks;
                hpEvents++;
            }

            Counters[SimCounter.HpEvents] = Counters[SimCounter.HpEvents] + hpEvents;
            Counters[SimCounter.StateEvents] = Counters[SimCounter.StateEvents] + stateEvents;
        }
    }

    /// <summary>체력 0 이하 몬스터 제거, 슬롯 반납.</summary>
    [BurstCompile(CompileSynchronously = true)]
    struct DeathJob : IJob
    {
        public int Capacity;
        [ReadOnly] public NativeArray<byte> Team;
        [ReadOnly] public NativeArray<int> Hp;
        public NativeArray<byte> Alive;
        public NativeArray<byte> Leak;
        public NativeArray<int> Counters;
        public NativeList<int> Free;

        public void Execute()
        {
            for (int m = 0; m < Capacity; m++)
            {
                if (Alive[m] == 0 || Hp[m] > 0) continue;

                Alive[m] = 0;
                int t = Team[m];
                Counters[SimCounter.DeathEvents] = Counters[SimCounter.DeathEvents] + 1;
                Counters[SimCounter.DeathsTeam0 + t] = Counters[SimCounter.DeathsTeam0 + t] + 1;
                if (Leak[m] != 0)
                {
                    Counters[SimCounter.LeaksTeam0 + t] = Counters[SimCounter.LeaksTeam0 + t] + 1;
                    Leak[m] = 0;
                }
                Free.Add(m);
            }
        }
    }

    /// <summary>이번 틱 위치를 랙 보정 기록에 저장.</summary>
    [BurstCompile(CompileSynchronously = true)]
    struct HistoryJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> Pos;
        [ReadOnly] public NativeArray<short> Lat;
        public NativeArray<int2> Out;

        public void Execute(int m)
        {
            Out[m] = new int2(Pos[m], Lat[m]);
        }
    }

    /// <summary>되감기 칼 판정. 칼 스윕(선분)에서 가장 먼저 닿는 적 몬스터를 찾는다.</summary>
    [BurstCompile(CompileSynchronously = true)]
    struct MeleeQueryJob : IJob
    {
        public LaneConfig Lane;
        public int Capacity;
        public int Tick;
        public int HistoryLength;
        [ReadOnly] public NativeArray<MonsterDef> Defs;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<int> SpawnTick;
        [ReadOnly] public NativeArray<int2> History;
        [ReadOnly] public NativeArray<int> CellStart;
        [ReadOnly] public NativeArray<int> CellItems;
        [ReadOnly] public NativeArray<MeleeQuery> Queries;
        public NativeArray<int> Results;

        public void Execute()
        {
            int cellCount = Lane.CellCount;
            int stride = cellCount + 1;
            int oldest = math.max(0, Tick - HistoryLength + 2);

            for (int q = 0; q < Queries.Length; q++)
            {
                var query = Queries[q];
                int enemy = 1 - query.AttackerTeam;
                int rt = math.clamp(query.RewindTick, oldest, Tick);
                int hb = (rt % HistoryLength) * Capacity;

                float2 a = query.Origin;
                float2 b = a + math.normalizesafe(query.Dir) * query.RangeMm;
                float2 ab = b - a;
                float len2 = math.lengthsq(ab);

                // 되감는 동안 몬스터가 움직인 거리만큼 칸 1개 여유
                float margin = query.RadiusMm + MonsterDefs.MaxRadiusMm + Lane.CellSizeMm;
                int c0 = Lane.CellOf((int)(math.min(a.x, b.x) - margin));
                int c1 = Lane.CellOf((int)(math.max(a.x, b.x) + margin));

                int best = -1;
                float bestT = float.MaxValue;

                for (int c = c0; c <= c1; c++)
                {
                    int s = CellStart[enemy * stride + c];
                    int e = CellStart[enemy * stride + c + 1];
                    for (int i = s; i < e; i++)
                    {
                        int o = CellItems[enemy * Capacity + i];
                        if (SpawnTick[o] > rt) continue; // 그 시점엔 없던 몬스터

                        int2 hp = History[hb + o];
                        float2 p = new float2(hp.x, hp.y);
                        float t = len2 > 0f ? math.saturate(math.dot(p - a, ab) / len2) : 0f;
                        float2 closest = a + ab * t;
                        float r = query.RadiusMm + Defs[Type[o]].RadiusMm;
                        if (math.lengthsq(p - closest) <= r * r && t < bestT)
                        {
                            bestT = t;
                            best = o;
                        }
                    }
                }

                Results[q] = best;
            }
        }
    }
}
