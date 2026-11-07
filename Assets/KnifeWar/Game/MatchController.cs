using System;
using System.Collections.Generic;
using KnifeWar.Core;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KnifeWar.Game
{
    public enum SlotKind : byte { Bot = 0, LocalHuman = 1, Remote = 2 }

    /// <summary>클라이언트가 서버에 보내는 한 틱 분량의 입력.</summary>
    public struct NetCommand
    {
        public uint Seq;
        public PlayerIntent Intent;
        public int ViewTick;     // 클라 화면에 보이던 다른 플레이어들의 서버 틱 (랙 보정 기준)
        public float ViewAlpha;
        public int MonsterTick;  // 클라 화면의 몬스터 시점
    }

    /// <summary>스냅샷 안의 플레이어 한 명.</summary>
    public struct PlayerNetState
    {
        public Vector3 Pos;
        public float Yaw, Pitch, Crouch;
        public bool Alive;
        public PlayerClass Class;
        public PlayerClass Pick;    // 준비 단계: King = 왕 희망(후보)
        public sbyte Vote;          // 준비 단계: 투표한 칸 (-1 없음)
        public int Hp;
        public byte TeleportSeq;
    }

    public sealed class SnapshotFrame
    {
        public int Tick;
        public readonly PlayerNetState[] Players = new PlayerNetState[MatchController.MaxPlayers];
    }

    /// <summary>
    /// 한 판을 진행한다. 역할에 따라 하는 일이 다르다.
    /// - Offline / Host / Server: 서버 권위 시뮬레이션 (라운드·이동·칼·몬스터 전부 여기서 결정)
    /// - Client: 서버 상태를 받아 보여주고, 내 캐릭터만 예측 이동한다
    ///
    /// 월드 좌표: 다리는 X축 방향, x = -60(블루 성문) ~ +60(레드 성문), 폭 12m, 다리 윗면 y = 0. 성 안은 MapLayout.
    /// </summary>
    public sealed class MatchController : MonoBehaviour
    {
        /// <summary>서버 틱 (64 기본, 128 선택). 서버는 실행 옵션 tick 128, 클라는 접속할 때 서버 값을 따른다.</summary>
        public static int TickRate { get; private set; } = 64;
        public static readonly int[] SupportedTickRates = { 64, 128 };
        public const int PlayersPerTeam = 6;
        public const int MaxPlayers = PlayersPerTeam * 2;
        /// <summary>레인 좌표 원점까지 거리 (m) = 성 뒤쪽. 다리 자체는 ±MapLayout.BridgeHalf.</summary>
        public const float LaneHalf = MapLayout.LaneHalf;
        /// <summary>다리 폭의 절반 (m). LaneConfig.HalfWidthMm 과 같은 값.</summary>
        public const float BridgeHalfWidth = 6f;
        public const float KillY = -6f;
        public const int PlayerLayer = 8;
        public const int MaxCommandsPerTick = 6;

        [Header("규칙 (임시값)")]
        public int RoundsToWin = 7;
        public int SelectSeconds = 12;
        public int RoundSeconds = 150;
        public int TeamMonsterCap = 400;

        [Header("클라이언트")]
        [Tooltip("다른 플레이어를 몇 틱 뒤에서 보간해서 보여줄지 (64틱 기준 4 = 62ms)")]
        public float InterpDelayTicks = 4f;

        [Header("머티리얼")]
        public Material BlueMat;
        public Material RedMat;
        public Material HatMat;
        public Material KnifeMat;
        public Material MonsterBlueMat;
        public Material MonsterRedMat;

        // ---- 상태 ----
        public NetRole Role { get; private set; }
        public int HumanIndex { get; private set; } = -1;
        public bool IsAuthority => Role == NetRole.Offline || Role == NetRole.Host || Role == NetRole.Server;
        public RoundRules Round { get; private set; }
        public WizardRules Wizard { get; private set; }
        public KnifeCombat Combat { get; private set; }
        public MonsterSim Monsters { get; private set; }
        public readonly PlayerAvatar[] Avatars = new PlayerAvatar[MaxPlayers];
        public readonly SlotKind[] Slots = new SlotKind[MaxPlayers];
        public int ServerTick { get; private set; }
        public PlayerAvatar Human => HumanIndex >= 0 ? Avatars[HumanIndex] : null;

        readonly BotBrain[] _bots = new BotBrain[MaxPlayers];
        readonly HumanInput _human = new HumanInput();
        PlayerIntent _humanIntent;

        readonly KnifeAttack[] _pendingAttack = new KnifeAttack[MaxPlayers];
        readonly sbyte[] _pendingCard = new sbyte[MaxPlayers];   // 손패 칸 (왕 카드 사용)
        readonly sbyte[] _pendingPick = new sbyte[MaxPlayers];
        readonly sbyte[] _pendingVote = new sbyte[MaxPlayers];
        readonly bool[] _pendingResurrect = new bool[MaxPlayers];   // 왕 현현 (마나 100)
        readonly int[] _viewTick = new int[MaxPlayers];
        readonly float[] _viewAlpha = new float[MaxPlayers];
        readonly int[] _monsterViewTick = new int[MaxPlayers];

        // 서버: 원격 플레이어 입력 큐
        readonly Queue<NetCommand>[] _commands = new Queue<NetCommand>[MaxPlayers];
        public readonly uint[] LastProcessedSeq = new uint[MaxPlayers];

        // 서버: 몬스터 변화 감지 (이벤트로 보내기 위해)
        byte[] _prevAlive, _prevState;
        public int MonsterCorrectionCursor;

        NativeArray<MeleeQuery> _mq;
        NativeArray<int> _mr;
        RoundPhase _lastPhase = (RoundPhase)255;
        int _matchSeed;   // 덱 섞기 시드 (서버만 의미 있음)

        /// <summary>이번 서버 틱에 일어난 일 (네트워크 서버가 클라에 보낸다).</summary>
        public readonly List<GameEvent> TickEvents = new List<GameEvent>();
        public event Action ServerTickCompleted;
        public event Action<NetCommand> ClientCommandReady;

        // ---- 클라이언트 ----
        readonly SnapshotFrame[] _frames = new SnapshotFrame[64];
        int _frameCount, _frameHead;
        double _clientClock;            // 클라가 추정한 서버 틱 (소수)
        bool _clockSynced;
        uint _nextSeq = 1;
        struct PredictedEntry { public NetCommand Cmd; public MoveState After; }
        readonly List<PredictedEntry> _predicted = new List<PredictedEntry>();
        byte _lastServerTeleportSeq;
        bool _hasTeleportSeq;
        int _localReadyTick;
        public int LastSnapshotTick { get; private set; }
        public float LastCorrectionError { get; private set; }
        public int CorrectionCount { get; private set; }

        // ---- 화면 ----
        public readonly List<KillFeedEntry> KillFeed = new List<KillFeedEntry>();
        public float HitMarkerUntil;
        public bool HitMarkerKill;
        public string LastSummonMessage;
        public float LastSummonMessageUntil;
        public readonly int[] Leaks = new int[2];
        public Camera HumanCamera { get; private set; }
        public GameFeedback Feedback { get; private set; }
        public HumanInput HumanControls => _human;
        public readonly int[] Kills = new int[MaxPlayers];
        public readonly int[] Deaths = new int[MaxPlayers];
        public readonly int[] DamageDealt = new int[MaxPlayers];

        /// <summary>테스트용: 내 캐릭터를 봇이 조종 (자동 접속 테스트 클라이언트).</summary>
        public bool HumanIsBot;

        /// <summary>Esc 메뉴 "경기 나가기" 처리 (네트워크 쪽이 설정. 없으면 씬을 다시 불러 메뉴로).</summary>
        public static Action LeaveHandler;

        // ==================================================================
        // 시작
        // ==================================================================

        void Awake()
        {
            Application.targetFrameRate = 240;
            Application.runInBackground = true;   // 창이 비활성이어도 서버·클라가 멈추지 않게 (멀티 필수)
            QualitySettings.vSyncCount = 0;
            Physics.IgnoreLayerCollision(PlayerLayer, PlayerLayer, true);

            var cfg = RoundConfig.Default;
            cfg.PlayersPerTeam = PlayersPerTeam;
            cfg.RoundsToWin = RoundsToWin;
            cfg.SelectSeconds = SelectSeconds;
            cfg.RoundSeconds = RoundSeconds;
            BuildSim(cfg, TickRate);
            _prevAlive = new byte[Monsters.Capacity];
            _prevState = new byte[Monsters.Capacity];
            _mq = new NativeArray<MeleeQuery>(1, Allocator.Persistent);
            _mr = new NativeArray<int>(1, Allocator.Persistent);
            for (int i = 0; i < _frames.Length; i++) _frames[i] = new SnapshotFrame();

            for (int p = 0; p < MaxPlayers; p++)
            {
                byte team = (byte)(p < PlayersPerTeam ? 0 : 1);
                Avatars[p] = CreateAvatar(p, team);
                Combat.Teams[p] = team;
                _commands[p] = new Queue<NetCommand>();
                _pendingCard[p] = -1;
                _pendingPick[p] = -1;
                _bots[p] = new BotBrain(Avatars[p], 1000 + p * 17);
                int local = p % PlayersPerTeam;
                _bots[p].PreferredClass = local == 5 ? PlayerClass.King : PlayerClass.Knight;   // 왕은 팀당 1명
            }
            GameSettings.Load();
            _matchSeed = UnityEngine.Random.Range(1, int.MaxValue);
        }

        void OnDestroy()
        {
            Combat?.Dispose();
            Monsters?.Dispose();
            if (_mq.IsCreated) _mq.Dispose();
            if (_mr.IsCreated) _mr.Dispose();
        }

        /// <summary>틱에 따라 달라지는 규칙·전투·몬스터를 (다시) 만든다. 판정 수치는 전부 ms 기준이라 틱이 바뀌어도 체감은 같다.</summary>
        void BuildSim(RoundConfig cfg, int tickRate)
        {
            TickRate = tickRate;
            Time.fixedDeltaTime = 1f / tickRate;
            InterpDelayTicks = Mathf.Round(0.0625f * tickRate);   // 다른 플레이어 보간 지연 62.5ms (64틱=4, 128틱=8)
            Combat?.Dispose();
            Monsters?.Dispose();
            Round = new RoundRules(cfg, tickRate);
            Wizard = new WizardRules(MaxPlayers, tickRate, TeamMonsterCap);
            Combat = new KnifeCombat(MaxPlayers, tickRate);
            Monsters = new MonsterSim(TeamMonsterCap * 2 + 64, tickRate, LaneConfig.Default);
            if (Avatars[0] != null)
                for (int p = 0; p < MaxPlayers; p++) Combat.Teams[p] = Avatars[p].Team;
        }

        /// <summary>틱 변경 (Begin 전에만). 64 또는 128.</summary>
        public bool SetTickRate(int tickRate)
        {
            if (Role != NetRole.None || Array.IndexOf(SupportedTickRates, tickRate) < 0) return false;
            if (tickRate == TickRate && Round.TickRate == tickRate) return true;
            BuildSim(Round.Config, tickRate);
            Debug.Log($"[KnifeWar] tick rate = {tickRate}");
            return true;
        }

        /// <summary>쇼케이스(스크린샷 자동 촬영)용: 봇이 고를 직업을 바꾼다.</summary>
        public void ShowcaseSetClass(int slot, PlayerClass c) { if (slot >= 0 && slot < MaxPlayers) _bots[slot].PreferredClass = c; }

        /// <summary>몇 선승인지 바꾼다 (Begin 전에만, 테스트용 짧은 경기 등).</summary>
        public void ConfigureRounds(int roundsToWin)
        {
            if (Role != NetRole.None || roundsToWin <= 0) return;
            RoundsToWin = roundsToWin;
            var cfg = Round.Config;
            cfg.RoundsToWin = roundsToWin;
            Round = new RoundRules(cfg, TickRate);
        }

        /// <summary>역할을 정하고 게임을 시작한다. humanSlot = 이 컴퓨터의 플레이어 칸 (없으면 -1).</summary>
        public void Begin(NetRole role, int humanSlot)
        {
            Role = role;
            HumanIndex = humanSlot;

            for (int p = 0; p < MaxPlayers; p++)
            {
                Slots[p] = p == humanSlot ? SlotKind.LocalHuman : SlotKind.Bot;
                Round.SetConnected(p, true);
                bool simulated = IsAuthority || p == humanSlot;
                Avatars[p].SetDrive(simulated ? AvatarDrive.Simulated : AvatarDrive.Interpolated);
                Avatars[p].LocalLook = p == humanSlot;
            }

            if (IsAuthority && humanSlot >= 0) Wizard.SetDeck(humanSlot, DeckStore.Current);   // 오프라인·호스트: 내 덱

            if (role != NetRole.Server)
            {
                ApplySkin(SkinSet.Current, humanSlot);
                gameObject.AddComponent<MonsterRenderer>().Init(this);
                gameObject.AddComponent<WorldEffects>().Init(this);
                CreateCamera();
                Feedback = gameObject.AddComponent<GameFeedback>();
                Feedback.Init(this);
                gameObject.AddComponent<GameHud>().Init(this);
            }
            if (humanSlot >= 0) Avatars[humanSlot].DisplayName = L.T("me");
            Debug.Log($"[KnifeWar] Begin role={role} humanSlot={humanSlot}");
        }

        // ---- 서버: 슬롯 관리 ----

        readonly string[] _reservedName = new string[MaxPlayers];

        /// <summary>매칭으로 정해진 칸 예약 (그 플레이어가 들어올 때까지는 봇이 대신 뛴다).</summary>
        public void ReserveSlot(int slot, string name)
        {
            if (slot < 0 || slot >= MaxPlayers) return;
            _reservedName[slot] = string.IsNullOrEmpty(name) ? null : name;
            if (_reservedName[slot] != null) Avatars[slot].DisplayName = _reservedName[slot];
            RosterChanged?.Invoke();
        }

        /// <summary>원격 플레이어에게 칸 배정. preferred가 있으면 그 칸, 없으면 사람이 적은 팀의 빈 칸.</summary>
        public int AssignRemoteSlot(int preferred = -1)
        {
            if (preferred >= 0 && preferred < MaxPlayers && Slots[preferred] == SlotKind.Bot)
            {
                Slots[preferred] = SlotKind.Remote;
                _commands[preferred].Clear();
                LastProcessedSeq[preferred] = 0;
                if (_reservedName[preferred] == null) Avatars[preferred].DisplayName = L.T("player_n", preferred + 1);
                Debug.Log($"[KnifeWar] remote player joined slot={preferred} ({Avatars[preferred].DisplayName})");
                RosterChanged?.Invoke();
                return preferred;
            }
            int best = -1, bestTeamHumans = int.MaxValue;
            for (int p = 0; p < MaxPlayers; p++)
            {
                if (Slots[p] != SlotKind.Bot || _reservedName[p] != null) continue;
                int humans = 0;
                for (int q = 0; q < MaxPlayers; q++)
                    if (Avatars[q].Team == Avatars[p].Team && Slots[q] != SlotKind.Bot) humans++;
                if (humans < bestTeamHumans) { bestTeamHumans = humans; best = p; }
            }
            if (best < 0) return -1;
            Slots[best] = SlotKind.Remote;
            _commands[best].Clear();
            LastProcessedSeq[best] = 0;
            Avatars[best].DisplayName = L.T("player_n", best + 1);
            Debug.Log($"[KnifeWar] remote player joined slot={best}");
            RosterChanged?.Invoke();
            return best;
        }

        public void ReleaseRemoteSlot(int slot)
        {
            if (slot < 0 || Slots[slot] != SlotKind.Remote) return;
            Slots[slot] = SlotKind.Bot;   // 나가면 봇이 이어받음
            Avatars[slot].DisplayName = _reservedName[slot] ?? BotName(slot);
            Debug.Log($"[KnifeWar] remote player left slot={slot}");
            RosterChanged?.Invoke();
        }

        public string BotName(int slot) => L.T(slot < PlayersPerTeam ? "team_blue" : "team_red") + (slot % PlayersPerTeam + 1);   // 아바타 생성 중에도 불리므로 칸 번호로 팀 판단

        /// <summary>칸 이름·사람 여부가 바뀌면 (서버가 클라에 로스터를 보내기 위해).</summary>
        public event Action RosterChanged;

        /// <summary>클라이언트: 서버가 보낸 이름 반영 (내 칸은 "나" 유지).</summary>
        public void ApplyRoster(int slot, string name)
        {
            if (slot < 0 || slot >= MaxPlayers || slot == HumanIndex || string.IsNullOrEmpty(name)) return;
            Avatars[slot].DisplayName = name;
        }

        public void EnqueueCommand(int slot, in NetCommand cmd)
        {
            if (slot < 0 || Slots[slot] != SlotKind.Remote) return;
            if (cmd.Seq <= LastProcessedSeq[slot]) return;
            foreach (var c in _commands[slot]) if (c.Seq == cmd.Seq) return;   // 중복 전송분
            if (_commands[slot].Count > 32) _commands[slot].Dequeue();
            _commands[slot].Enqueue(cmd);
        }

        // ==================================================================
        // 프레임
        // ==================================================================

        void Update()
        {
            if (Role == NetRole.None) return;

            if (HumanIndex >= 0 && !HumanIsBot)
            {
                var i = _human.Sample(Round.Phase);
                _humanIntent = i;
                if (i.Attack != KnifeAttack.None) _pendingAttack[HumanIndex] = i.Attack;
                if (i.Summon >= 0) _pendingCard[HumanIndex] = i.Summon;
                if (i.Resurrect) _pendingResurrect[HumanIndex] = true;
                if (i.PickClass >= 0) _pendingPick[HumanIndex] = i.PickClass;
                var h = Human;
                h.LookYaw = i.Yaw;
                h.LookPitch = i.Pitch;
            }

            if (Role == NetRole.Client) ClientInterpolate();

            if (Round.Phase == RoundPhase.MatchEnd && Input.GetKeyDown(KeyCode.R) && Role != NetRole.Client)
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);

            UpdateSpectatorCamera(Time.deltaTime);
        }

        void FixedUpdate()
        {
            if (Role == NetRole.None) return;
            if (IsAuthority) ServerTickStep();
            else ClientTickStep();
        }

        // ==================================================================
        // 서버 틱
        // ==================================================================

        void ServerTickStep()
        {
            TickEvents.Clear();
            int tick = ServerTick;
            float dt = Time.fixedDeltaTime;
            Round.Tick(tick, _tieBreak ??= MonsterTieBreak);
            var phase = Round.Phase;
            if (phase != _lastPhase) OnPhaseEntered(phase);
            _lastPhase = phase;

            // ---- 이동 (입력 → 시뮬레이션) ----
            for (int p = 0; p < MaxPlayers; p++)
            {
                var a = Avatars[p];
                a.Frozen = phase != RoundPhase.Live;
                bool buffed = a.Class == PlayerClass.Knight && Wizard.TeamBuffs[a.Team].IsActive(tick);
                a.SpeedMultiplier = buffed ? BuffType.WarHornSpeed : 1f;

                switch (Slots[p])
                {
                    case SlotKind.Bot:
                    {
                        var i = _bots[p].Think(this, Time.time, dt);
                        CollectEdges(p, i);
                        _viewTick[p] = Combat.Tick - 1;
                        _viewAlpha[p] = 0f;
                        _monsterViewTick[p] = Monsters.Tick;
                        a.SimulateTick(i, dt);
                        break;
                    }
                    case SlotKind.LocalHuman:
                    {
                        var i = _humanIntent;
                        if (HumanIsBot)
                        {
                            i = _bots[p].Think(this, Time.time, dt);
                            CollectEdges(p, i);
                        }
                        _viewTick[p] = Combat.Tick - 1;
                        _viewAlpha[p] = 0f;
                        _monsterViewTick[p] = Monsters.Tick;
                        a.SimulateTick(i, dt);
                        break;
                    }
                    case SlotKind.Remote:
                    {
                        var q = _commands[p];
                        int n = 0;
                        while (q.Count > 0 && n < MaxCommandsPerTick)
                        {
                            var c = q.Dequeue();
                            if (c.Seq <= LastProcessedSeq[p]) continue;
                            CollectEdges(p, c.Intent);
                            _viewTick[p] = c.ViewTick;
                            _viewAlpha[p] = c.ViewAlpha;
                            _monsterViewTick[p] = c.MonsterTick;
                            a.SimulateTick(c.Intent, dt);
                            LastProcessedSeq[p] = c.Seq;
                            n++;
                        }
                        break;
                    }
                }
            }

            // 왕 희망·투표 (투표 라운드 준비 단계에서만 받는다)
            for (int p = 0; p < MaxPlayers; p++)
            {
                if (_pendingPick[p] >= 0) Round.PickClass(p, (PlayerClass)Mathf.Clamp(_pendingPick[p], 0, 1));
                if (_pendingVote[p] >= 0) Round.Vote(p, _pendingVote[p] == VoteCancel ? -1 : _pendingVote[p]);
                _pendingPick[p] = -1;
                _pendingVote[p] = -1;
            }

            // 추락
            if (phase == RoundPhase.Live)
                for (int p = 0; p < MaxPlayers; p++)
                    if (Avatars[p].Alive && Avatars[p].transform.position.y < KillY) Kill(p, -1, KillHow.Fall);

            // ---- 칼 ----
            for (int p = 0; p < MaxPlayers; p++)
            {
                var a = Avatars[p];
                Combat.Poses[p] = new PlayerPose
                {
                    Root = a.transform.position,
                    Yaw = a.Yaw * Mathf.Deg2Rad,
                    Crouch = a.Crouch,
                    Alive = (byte)(a.Alive ? 1 : 0),
                };
                Combat.CanAttack[p] = (byte)(phase == RoundPhase.Live && a.Class == PlayerClass.Knight ? 1 : 0);

                var atk = _pendingAttack[p];
                _pendingAttack[p] = KnifeAttack.None;
                bool wasBusy = Combat.IsBusy(p);
                Combat.SubmitInput(p, new KnifeInput
                {
                    Pressed = atk,
                    Yaw = a.Yaw * Mathf.Deg2Rad,
                    Pitch = -a.Pitch * Mathf.Deg2Rad,
                    ViewTick = _viewTick[p],
                    ViewAlpha = _viewAlpha[p],
                });
                if (atk != KnifeAttack.None && !wasBusy && Combat.IsBusy(p))
                    Emit(new GameEvent { Type = GameEventType.Swing, A = p, B = (int)atk });
            }

            Combat.Step();
            for (int e = 0; e < Combat.Events.Length; e++)
            {
                var ev = Combat.Events[e];
                Emit(new GameEvent
                {
                    Type = GameEventType.Hit, A = ev.Attacker, B = ev.Victim, C = ev.Damage, D = (int)ev.Part, E = ev.Kill ? 1 : 0,
                });
                if (ev.Kill)
                {
                    var how = ev.Attack == KnifeAttack.Stab ? KillHow.Stab : KillHow.Slash;   // 부위·뒤치기 구분 없음
                    Kill(ev.Victim, ev.Attacker, how);
                }
            }

            // ---- 몬스터 ----
            Monsters.PrepareTick();
            Monsters.TeamFire[0] = (byte)(Wizard.FireActive(0, tick) ? 1 : 0);
            Monsters.TeamFire[1] = (byte)(Wizard.FireActive(1, tick) ? 1 : 0);
            if (phase == RoundPhase.Live)
            {
                for (int p = 0; p < MaxPlayers; p++)
                {
                    if (_pendingCard[p] >= 0)
                    {
                        bool fieldBefore0 = Wizard.TeamField[0].IsActive(tick), fieldBefore1 = Wizard.TeamField[1].IsActive(tick);
                        var r = Wizard.TryPlay(p, _pendingCard[p], Round, Monsters, tick, out int cardId);
                        if (r == WizardRules.SummonResult.Ok)
                        {
                            Emit(new GameEvent { Type = GameEventType.Summon, A = p, B = cardId });
                            int team = Round.Team[p];
                            if (Wizard.TeamField[team].IsActive(tick) != (team == 0 ? fieldBefore0 : fieldBefore1))
                                Emit(new GameEvent { Type = GameEventType.Field, A = team, B = Wizard.TeamField[team].BuffId, D = Wizard.TeamField[team].EndTick });
                        }
                        else if (r != WizardRules.SummonResult.Dead) Emit(new GameEvent { Type = GameEventType.Message, A = p, B = (int)SummonMessage(r) });
                    }
                    _pendingCard[p] = -1;
                    if (_pendingResurrect[p])
                    {
                        _pendingResurrect[p] = false;
                        var r = Wizard.TryResurrect(p, Round, out int revived);
                        if (r == WizardRules.SummonResult.Ok) Revive(revived, p);
                        else if (r != WizardRules.SummonResult.Dead) Emit(new GameEvent { Type = GameEventType.Message, A = p, B = (int)SummonMessage(r) });
                    }
                }
                Wizard.Tick(tick, Round, Monsters);   // 마나 회복 + 병영 생산
                Monsters.TeamFire[0] = (byte)(Wizard.FireActive(0, tick) ? 1 : 0);
                Monsters.TeamFire[1] = (byte)(Wizard.FireActive(1, tick) ? 1 : 0);
            }
            else
            {
                for (int p = 0; p < MaxPlayers; p++) { _pendingCard[p] = -1; _pendingResurrect[p] = false; }
            }

            for (int s = 0; s < Combat.Swings.Length; s++)
            {
                var sw = Combat.Swings[s];
                if (!sw.HitPlayer) HitMonster(sw);
            }

            for (int p = 0; p < MaxPlayers; p++)
            {
                var a = Avatars[p];
                Vector3 pos = a.transform.position;
                bool target = phase == RoundPhase.Live && a.Alive && pos.y > -1f;
                Monsters.SetPlayer(p, LaneX(pos.x), (int)(pos.z * 1000f), a.Team, target, Round.Class[p] == PlayerClass.King);
            }

            Monsters.Step();
            Leaks[0] += Monsters.Counters[SimCounter.LeaksTeam0];
            Leaks[1] += Monsters.Counters[SimCounter.LeaksTeam1];
            if (phase == RoundPhase.Live) ApplyMonsterDamage();
            EmitMonsterChanges();

            Combat.EndTick();
            for (int p = 0; p < MaxPlayers; p++) Avatars[p].CommitTick();
            ServerTick++;

            // 호스트·오프라인은 이 컴퓨터에서도 결과를 보여준다
            if (Role != NetRole.Server)
                for (int i = 0; i < TickEvents.Count; i++) HandleEventLocal(TickEvents[i]);
            else
                for (int i = 0; i < TickEvents.Count; i++) RecordStats(TickEvents[i]);

            ServerTickCompleted?.Invoke();
        }

        void CollectEdges(int p, in PlayerIntent i)
        {
            if (i.Attack != KnifeAttack.None) _pendingAttack[p] = i.Attack;
            if (i.Summon >= 0) _pendingCard[p] = i.Summon;
            if (i.PickClass >= 0) _pendingPick[p] = i.PickClass;
            if (i.Vote >= 0) _pendingVote[p] = i.Vote;
            if (i.Resurrect) _pendingResurrect[p] = true;
        }

        /// <summary>Intent.Vote 에 이 값이면 투표 취소.</summary>
        public const sbyte VoteCancel = 127;

        Func<int> _tieBreak;

        /// <summary>시간 초과 + 생존자 수 동률: 필드에 남은 몬스터 체력 합이 큰 팀이 이긴다 (왕의 소환이 승부를 가름). 같으면 무승부.</summary>
        int MonsterTieBreak()
        {
            var s = Monsters;
            s.Complete();
            long hp0 = 0, hp1 = 0;
            for (int m = 0; m < s.Capacity; m++)
            {
                if (s.Alive[m] == 0) continue;
                if (s.Team[m] == 0) hp0 += Math.Max(0, s.Hp[m]); else hp1 += Math.Max(0, s.Hp[m]);
            }
            return hp0 == hp1 ? -1 : (hp0 > hp1 ? 0 : 1);
        }
        void Emit(in GameEvent e) => TickEvents.Add(e);

        static GameMessage SummonMessage(WizardRules.SummonResult r)
        {
            switch (r)
            {
                case WizardRules.SummonResult.NoMana: return GameMessage.NoMana;
                case WizardRules.SummonResult.TeamCap: return GameMessage.TeamCap;
                case WizardRules.SummonResult.NotWizard: return GameMessage.NotKing;
                case WizardRules.SummonResult.Limit: return GameMessage.Limit;
                case WizardRules.SummonResult.NoDead: return GameMessage.NoDead;
                default: return GameMessage.SummonFail;
            }
        }

        void OnPhaseEntered(RoundPhase phase)
        {
            Feedback?.OnPhase(phase, Round.LastRoundWinner, Human != null ? Human.Team : (byte)0);
            if (phase == RoundPhase.RoundEnd || phase == RoundPhase.MatchEnd)
                Debug.Log($"[KnifeWar] {phase} winner={Round.LastRoundWinner} score={Round.Score[0]}:{Round.Score[1]}");
            switch (phase)
            {
                case RoundPhase.Select:
                    Monsters.Clear();
                    for (int p = 0; p < MaxPlayers; p++)
                    {
                        var a = Avatars[p];
                        a.SetAlive(true);
                        a.SetClass(Round.Class[p]);
                        Spawn(p);
                    }
                    Leaks[0] = Leaks[1] = 0;
                    SelectCursor();
                    break;

                case RoundPhase.Live:
                    Debug.Log($"[KnifeWar] round {Round.RoundNumber} live");
                    Combat.ResetForRound();
                    Wizard.ResetForRound(Round.RoundNumber, _matchSeed);
                    Monsters.Complete();
                    Monsters.TeamFire[0] = Monsters.TeamFire[1] = 0;
                    for (int p = 0; p < MaxPlayers; p++)
                    {
                        bool wasKing = Avatars[p].Class == PlayerClass.King;
                        Avatars[p].SetClass(Round.Class[p]);
                        Avatars[p].SetAlive(Round.Alive[p]);
                        if (wasKing != (Round.Class[p] == PlayerClass.King)) Spawn(p);   // 투표로 왕이 바뀌면 자리(2층/1층)도 바꿔 준다
                        _bots[p].OnRoundStart(Time.time);
                    }
                    break;
            }
        }

        /// <summary>스폰: 기사는 성 1층 홀, 왕은 2층 테이블 앞. 둘 다 적 쪽을 본다.</summary>
        void Spawn(int p)
        {
            var a = Avatars[p];
            int local = p % PlayersPerTeam;
            float side = a.Team == 0 ? -1f : 1f;
            float yaw = a.Team == 0 ? 90f : -90f;
            if (a.Class == PlayerClass.King)
            {
                a.Teleport(new Vector3(side * (MapLayout.TableX - 1.6f), MapLayout.Floor2Y + 0.05f, 0f), yaw);
            }
            else
            {
                float x = side * (MapLayout.GateX + 2.2f + (local / 3) * 1.5f);
                float z = (local % 3 - 1) * 1.6f;
                a.Teleport(new Vector3(x, 0.05f, z), yaw);
            }
            if (p == HumanIndex) _human.Reset(yaw);
        }

        void Kill(int victim, int attacker, KillHow how)
        {
            var a = Avatars[victim];
            if (!a.Alive) return;
            Combat.Hp[victim] = 0;
            a.SetAlive(false);
            Round.Kill(victim);
            Emit(new GameEvent { Type = GameEventType.Kill, A = victim, B = attacker, C = (int)how });
            Debug.Log($"[KnifeWar] kill t={Time.timeSinceLevelLoad:0.0}s {(attacker >= 0 ? Avatars[attacker].DisplayName : "-")} -> {a.DisplayName} ({GameText.How(how)})");
        }

        /// <summary>현현: 죽은 기사를 성 1층에서 체력 가득으로 되살린다 (서버).</summary>
        void Revive(int player, int byKing)
        {
            var a = Avatars[player];
            Combat.Hp[player] = KnifeTuning.MaxHp;
            a.SetAlive(true);
            Spawn(player);
            Emit(new GameEvent { Type = GameEventType.Revive, A = player, B = byKing });
            Debug.Log($"[KnifeWar] revive t={Time.timeSinceLevelLoad:0.0}s {Avatars[byKing].DisplayName} -> {a.DisplayName}");
        }

        // ---- 몬스터 ----

        public static int LaneX(float worldX) => MapLayout.LaneX(worldX);
        public static float WorldX(int laneX) => MapLayout.WorldX(laneX);

        void HitMonster(in KnifeSwing sw)
        {
            var def = Combat.Def(sw.Attack);
            float3 d = sw.Sweep.Dir;
            float flat = math.sqrt(d.x * d.x + d.z * d.z);
            if (flat < 0.2f) return;

            _mq[0] = new MeleeQuery
            {
                Origin = new float2(LaneX(sw.Sweep.Eye.x), sw.Sweep.Eye.z * 1000f),
                Dir = new float2(d.x / flat, d.z / flat),
                RangeMm = def.Range * flat * 1000f,
                RadiusMm = def.Radius * 1000f,
                // 클라 화면에 보이던 몬스터 시점으로 되감기 (이번 틱은 아직 Step 전이라 최대 Tick-1)
                RewindTick = math.min(_monsterViewTick[sw.Attacker], Monsters.Tick - 1),
                AttackerTeam = Combat.Teams[sw.Attacker],
            };
            // 날아오는 화살 부수기 (몬스터를 맞추든 말든)
            Monsters.CutArrows(_mq[0].AttackerTeam, _mq[0].Origin, _mq[0].Dir, _mq[0].RangeMm, _mq[0].RadiusMm);
            Monsters.QueryMelee(_mq, _mr);
            int slot = _mr[0];
            if (slot < 0) return;
            bool parried = Monsters.ApplyDamage(slot, def.Damage);
            Emit(new GameEvent
            {
                Type = GameEventType.MonsterHit, A = sw.Attacker, C = def.Damage,
                F1 = WorldX(Monsters.Pos[slot]), F2 = Monsters.Lat[slot] / 1000f,
            });
            if (parried) Emit(new GameEvent { Type = GameEventType.MonsterParry, A = sw.Attacker, F1 = WorldX(Monsters.Pos[slot]), F2 = Monsters.Lat[slot] / 1000f });
        }

        void ApplyMonsterDamage()
        {
            for (int p = 0; p < MaxPlayers; p++)
            {
                int dmg = Monsters.PlayerDamage[p];
                if (dmg <= 0 || !Avatars[p].Alive) continue;
                Combat.Hp[p] = math.max(0, Combat.Hp[p] - dmg);
                bool kill = Combat.Hp[p] == 0;
                Emit(new GameEvent { Type = GameEventType.MonsterHurt, A = p, C = dmg, E = kill ? 1 : 0 });
                if (kill) Kill(p, -1, KillHow.Monster);
            }
        }

        /// <summary>몬스터 소환·상태 변화·사망을 이벤트로 만든다 (클라는 이것만으로 몬스터를 재현).</summary>
        void EmitMonsterChanges()
        {
            var s = Monsters;
            for (int m = 0; m < s.Capacity; m++)
            {
                byte alive = s.Alive[m], state = s.State[m];
                if (alive != 0 && (_prevAlive[m] == 0 || s.SpawnTick[m] == s.Tick))
                {
                    Emit(new GameEvent
                    {
                        Type = GameEventType.MonsterSpawn, A = m, B = s.Team[m], C = s.Type[m], D = s.Pos[m], E = s.Lat[m], F = state,
                    });
                }
                else if (alive == 0 && _prevAlive[m] != 0)
                {
                    Emit(new GameEvent { Type = GameEventType.MonsterDeath, A = m });
                }
                else if (alive != 0 && state != _prevState[m])
                {
                    Emit(new GameEvent { Type = GameEventType.MonsterState, A = m, B = state, D = s.Pos[m], E = s.Lat[m] });
                }
                _prevAlive[m] = alive;
                _prevState[m] = state;
            }
            for (int i = 0; i < MonsterSim.MaxArrows; i++)
            {
                if (s.ArrowEnd[i] != 0)
                    Emit(new GameEvent { Type = GameEventType.ArrowEnd, A = s.ArrowId[i], B = s.ArrowEnd[i], C = s.ArrowPos[i], D = s.ArrowLat[i] });
                else if (s.ArrowAlive[i] != 0 && s.ArrowSpawnTick[i] == s.Tick)
                    Emit(new GameEvent { Type = GameEventType.ArrowSpawn, A = s.ArrowId[i], B = s.ArrowTeam[i], C = s.ArrowPos[i], D = s.ArrowLat[i], E = s.ArrowVx[i], F = s.ArrowVy[i], F1 = s.ArrowLife[i] });
            }
        }

        // ==================================================================
        // 이벤트 → 화면·통계 (호스트와 클라 공통)
        // ==================================================================

        void RecordStats(in GameEvent e)
        {
            switch (e.Type)
            {
                case GameEventType.Kill:
                    Deaths[e.A]++;
                    if (e.B >= 0) Kills[e.B]++;
                    break;
                case GameEventType.Hit:
                case GameEventType.MonsterHit:
                    DamageDealt[e.A] += e.C;
                    break;
            }
        }

        public void HandleEventLocal(in GameEvent e)
        {
            RecordStats(e);
            switch (e.Type)
            {
                case GameEventType.Kill:
                {
                    var v = Avatars[e.A];
                    var how = (KillHow)e.C;
                    KillFeed.Add(new KillFeedEntry
                    {
                        Killer = e.B >= 0 ? Avatars[e.B].DisplayName : (how == KillHow.Fall ? "" : L.T("monster")),
                        KillerTeam = e.B >= 0 ? Avatars[e.B].Team : (byte)(1 - v.Team),
                        Victim = v.DisplayName,
                        VictimTeam = v.Team,
                        How = GameText.How(how),
                        Time = Time.time,
                    });
                    if (KillFeed.Count > 6) KillFeed.RemoveAt(0);
                    if (Role == NetRole.Client) Debug.Log($"[KnifeWar] (client) kill {KillFeed[KillFeed.Count - 1].Killer} -> {v.DisplayName} ({GameText.How(how)})");
                    break;
                }
                case GameEventType.Hit:
                    if (e.A == HumanIndex)
                    {
                        HitMarkerUntil = Time.time + 0.25f;
                        HitMarkerKill = e.E != 0;
                        if (Role == NetRole.Client) Debug.Log($"[KnifeWar] (client) my hit -> slot {e.B} dmg={e.C} part={(HitPart)e.D}");
                    }
                    Feedback?.OnPlayerHit(e.A, e.B, e.C, (HitPart)e.D, e.E != 0, Avatars[e.B].transform.position);
                    break;
                case GameEventType.MonsterHit:
                    if (e.A == HumanIndex)
                    {
                        HitMarkerUntil = Time.time + 0.15f;
                        HitMarkerKill = false;
                    }
                    Feedback?.OnMonsterHit(e.A, new Vector3(e.F1, 0f, e.F2), e.C);
                    break;
                case GameEventType.MonsterHurt:
                    Feedback?.OnMonsterDamagedPlayer(e.A, e.C, e.E != 0);
                    break;
                case GameEventType.Swing:
                    // 클라의 내 캐릭터는 클릭 순간 이미 휘둘렀다 (예측)
                    if (!(Role == NetRole.Client && e.A == HumanIndex))
                    {
                        Avatars[e.A].PlaySwing((KnifeAttack)e.B);
                        Feedback?.OnSwing(e.A, (KnifeAttack)e.B);
                    }
                    break;
                case GameEventType.Summon:
                    Feedback?.OnSummon(e.A);
                    if (Avatars[e.A].Model != null) Avatars[e.A].Model.Summon();
                    if (e.A == HumanIndex && CardDefs.IsValidId(e.B)) ShowMessage(L.T("card_used", L.T("card_" + CardDefs.Get(e.B).Key + "_name")));
                    break;
                case GameEventType.Field:
                    if (Role == NetRole.Client) Wizard.SetFieldMirror(e.A, e.B, e.D);
                    Feedback?.OnField(e.A, e.B);
                    break;
                case GameEventType.Message:
                    if (e.A == HumanIndex) ShowMessage(GameText.Message((GameMessage)e.B));
                    break;
                case GameEventType.MonsterSpawn:
                    if (Role == NetRole.Client) Monsters.PuppetSpawn(e.A, (byte)e.B, (byte)e.C, e.D, (short)e.E, (byte)e.F);
                    break;
                case GameEventType.MonsterState:
                    if (Role == NetRole.Client) Monsters.PuppetSetState(e.A, (byte)e.B, e.D, e.E);
                    break;
                case GameEventType.MonsterDeath:
                    if (Role == NetRole.Client) Monsters.PuppetRemove(e.A);
                    break;
                case GameEventType.ArrowSpawn:
                    if (Role == NetRole.Client) Monsters.PuppetSpawnArrow(e.A, (byte)e.B, e.C, e.D, e.E, e.F, (int)e.F1);
                    Feedback?.OnArrowShot(WorldX(e.C), e.D / 1000f);
                    break;
                case GameEventType.ArrowEnd:
                    if (Role == NetRole.Client) Monsters.PuppetEndArrow(e.A, (byte)e.B);
                    Feedback?.OnArrowEnd((byte)e.B, WorldX(e.C), e.D / 1000f);
                    break;
                case GameEventType.MonsterParry:
                    Feedback?.OnParry(e.A, e.F1, e.F2);
                    if (e.A == HumanIndex) ShowMessage(L.T("parry"));
                    break;
                case GameEventType.Revive:
                    Feedback?.OnRevive(e.A);
                    KillFeed.Add(new KillFeedEntry { Killer = Avatars[e.B].DisplayName, KillerTeam = Avatars[e.B].Team, Victim = Avatars[e.A].DisplayName, VictimTeam = Avatars[e.A].Team, How = L.T("how_revive"), Time = Time.time });
                    if (e.A == HumanIndex) ShowMessage(L.T("revived_you"));
                    else if (e.B == HumanIndex) ShowMessage(L.T("card_used", L.T("card_resurrect_name")));
                    break;
            }
        }

        // ==================================================================
        // 클라이언트
        // ==================================================================

        void ClientTickStep()
        {
            // 서버와 같은 규칙(적 왕 쪽으로)으로 몬스터를 미리 움직여 둔다 (상태가 바뀌면 서버 값으로 맞춤)
            for (int p = 0; p < MaxPlayers; p++)
            {
                var a = Avatars[p];
                Vector3 pos = a.transform.position;
                Monsters.SetPlayer(p, LaneX(pos.x), (int)(pos.z * 1000f), a.Team, Round.Phase == RoundPhase.Live && a.Alive && pos.y > -1f, Round.Class[p] == PlayerClass.King);
            }
            Monsters.PuppetStep();
            if (!_clockSynced || HumanIndex < 0) return;

            var me = Human;
            int estTick = (int)_clientClock;
            me.Frozen = Round.Phase != RoundPhase.Live;
            bool buffed = me.Class == PlayerClass.Knight && Wizard.TeamBuffs[me.Team].IsActive(estTick);
            me.SpeedMultiplier = buffed ? BuffType.WarHornSpeed : 1f;

            PlayerIntent intent;
            if (HumanIsBot)
            {
                intent = _bots[HumanIndex].Think(this, Time.time, Time.fixedDeltaTime);
                me.LookYaw = intent.Yaw;
                me.LookPitch = intent.Pitch;
            }
            else
            {
                intent = _humanIntent;
                intent.Attack = _pendingAttack[HumanIndex];
                intent.Summon = _pendingCard[HumanIndex];
                intent.Resurrect = _pendingResurrect[HumanIndex];
                intent.PickClass = _pendingPick[HumanIndex];
                intent.Vote = _pendingVote[HumanIndex];
            }
            _pendingAttack[HumanIndex] = KnifeAttack.None;
            _pendingCard[HumanIndex] = -1;
            _pendingResurrect[HumanIndex] = false;
            _pendingPick[HumanIndex] = -1;
            _pendingVote[HumanIndex] = -1;

            double view = _clientClock - InterpDelayTicks;
            var cmd = new NetCommand
            {
                Seq = _nextSeq++,
                Intent = intent,
                ViewTick = (int)Math.Floor(view),
                ViewAlpha = (float)(view - Math.Floor(view)),
                MonsterTick = estTick,
            };

            // 내 칼 휘두르기는 즉시 보여준다 (판정은 서버)
            if (intent.Attack != KnifeAttack.None && Round.Phase == RoundPhase.Live && me.Alive
                && me.Class == PlayerClass.Knight && estTick >= _localReadyTick)
            {
                var def = Combat.Def(intent.Attack);
                _localReadyTick = estTick + def.WindupTicks + def.RecoveryTicks;
                me.PlaySwing(intent.Attack);
                Feedback?.OnSwing(HumanIndex, intent.Attack);
            }

            me.SimulateTick(intent, Time.fixedDeltaTime);
            me.CommitTick();
            _predicted.Add(new PredictedEntry { Cmd = cmd, After = me.GetMoveState() });
            if (_predicted.Count > 256) _predicted.RemoveAt(0);

            _clientClock += 1.0;
            ClientCommandReady?.Invoke(cmd);
        }

        /// <summary>스냅샷에 실리는 팀 공통 왕 정보 (버프·필드·건물).</summary>
        public struct TeamKingState
        {
            public int BuffEnd;      // 나팔 등 기사 버프 끝 틱 (0 = 없음)
            public int FieldId;      // 필드 효과 id (-1 없음)
            public int FieldEnd;
            public byte Barracks, Wells;
        }

        /// <summary>스냅샷에 실리는 내 손패.</summary>
        public struct HandState
        {
            public byte C0, C1, C2, C3, Next;
        }

        /// <summary>서버 스냅샷 수신.</summary>
        public void ClientReceiveSnapshot(SnapshotFrame frame, uint ackSeq, Vector3 myVelocity, float myMana, in HandState hand,
            RoundPhase phase, int roundNumber, int phaseEndTick, int score0, int score1, int lastWinner, int matchWinner,
            TeamKingState team0, TeamKingState team1)
        {
            if (frame.Tick < LastSnapshotTick) return;   // 순서가 뒤바뀐 옛 스냅샷은 버림

            var slot = _frames[_frameHead];
            slot.Tick = frame.Tick;
            Array.Copy(frame.Players, slot.Players, MaxPlayers);
            _frameHead = (_frameHead + 1) % _frames.Length;
            _frameCount = Math.Min(_frameCount + 1, _frames.Length);
            LastSnapshotTick = frame.Tick;

            // 시계: 클라 시계는 최신 스냅샷 틱을 따라가되 부드럽게
            if (!_clockSynced || Math.Abs(_clientClock - frame.Tick) > TickRate / 8) { _clientClock = frame.Tick; _clockSynced = true; }
            else _clientClock += (frame.Tick - _clientClock) * 0.1;
            ServerTick = frame.Tick;

            var prevPhase = Round.Phase;
            Round.ApplyMirror(phase, roundNumber, phaseEndTick, score0, score1, lastWinner, matchWinner);
            if (prevPhase != phase)
            {
                Feedback?.OnPhase(phase, lastWinner, Human != null ? Human.Team : (byte)0);
                if (phase == RoundPhase.Select) { Monsters.Clear(); SelectCursor(); }
                else if (phase == RoundPhase.Live) LockCursor();
            }
            ApplyTeamKingMirror(0, team0);
            ApplyTeamKingMirror(1, team1);

            for (int p = 0; p < MaxPlayers; p++)
            {
                var ps = frame.Players[p];
                Combat.Hp[p] = ps.Hp;
                Round.Class[p] = ps.Class;
                Round.Alive[p] = ps.Alive;
                Round.ApplyPickMirror(p, ps.Pick);
                Round.ApplyVoteMirror(p, ps.Vote);
                var a = Avatars[p];
                a.SetClass(ps.Class);
                if (a.Alive != ps.Alive) a.SetAlive(ps.Alive);
            }

            if (HumanIndex < 0) return;
            Wizard.SetManaMirror(HumanIndex, myMana);
            Wizard.SetHandMirror(HumanIndex, hand.C0, hand.C1, hand.C2, hand.C3, hand.Next);
            Reconcile(frame.Players[HumanIndex], ackSeq, myVelocity);
        }

        void ApplyTeamKingMirror(int team, in TeamKingState t)
        {
            if (t.BuffEnd > 0) Wizard.TeamBuffs[team].Apply(BuffType.WarHorn, t.BuffEnd); else Wizard.TeamBuffs[team].Clear();
            Wizard.SetFieldMirror(team, t.FieldId, t.FieldEnd);
            Wizard.Buildings[team].Set(BuildingType.Barracks, t.Barracks);
            Wizard.Buildings[team].Set(BuildingType.ManaWell, t.Wells);
            Monsters.Complete();
            Monsters.TeamFire[team] = (byte)(Wizard.FireActive(team, ServerTick) ? 1 : 0);
        }

        /// <summary>서버: 스냅샷용 팀 왕 정보.</summary>
        public TeamKingState TeamKing(int team)
        {
            var b = Wizard.TeamBuffs[team];
            var f = Wizard.TeamField[team];
            return new TeamKingState
            {
                BuffEnd = b.IsActive(ServerTick) ? b.EndTick : 0,
                FieldId = f.IsActive(ServerTick) ? f.BuffId : -1,
                FieldEnd = f.IsActive(ServerTick) ? f.EndTick : 0,
                Barracks = (byte)Wizard.Buildings[team].Count(BuildingType.Barracks),
                Wells = (byte)Wizard.Buildings[team].Count(BuildingType.ManaWell),
            };
        }

        public HandState HandOf(int player) => new HandState
        {
            C0 = Wizard.Hand(player, 0), C1 = Wizard.Hand(player, 1), C2 = Wizard.Hand(player, 2), C3 = Wizard.Hand(player, 3),
            Next = Wizard.NextCard(player),
        };

        void Reconcile(in PlayerNetState server, uint ackSeq, Vector3 serverVel)
        {
            var me = Human;

            // 서버가 순간이동시킴 (라운드 시작 스폰 등) → 그 위치로 바로
            if (!_hasTeleportSeq || server.TeleportSeq != _lastServerTeleportSeq)
            {
                _hasTeleportSeq = true;
                _lastServerTeleportSeq = server.TeleportSeq;
                me.Teleport(server.Pos, server.Yaw);
                _human.Reset(server.Yaw);
                _predicted.Clear();
                return;
            }

            int idx = -1;
            for (int i = 0; i < _predicted.Count; i++)
                if (_predicted[i].Cmd.Seq == ackSeq) { idx = i; break; }
            if (idx < 0) return;

            var predicted = _predicted[idx].After;
            _predicted.RemoveRange(0, idx + 1);
            float err = Vector3.Distance(predicted.Position, server.Pos);
            LastCorrectionError = err;
            if (err < 0.03f) return;

            // 오차 → 서버 상태로 되돌리고 아직 서버가 처리 안 한 입력을 다시 실행
            CorrectionCount++;
            me.SetMoveState(new MoveState { Position = server.Pos, Velocity = serverVel, Crouch = server.Crouch });
            for (int i = 0; i < _predicted.Count; i++)
            {
                var e = _predicted[i];
                me.SimulateTick(e.Cmd.Intent, Time.fixedDeltaTime);
                e.After = me.GetMoveState();
                _predicted[i] = e;
            }
            me.CommitTick();
            me.CommitTick();
        }

        /// <summary>다른 플레이어를 InterpDelayTicks 만큼 과거 시점으로 보간해서 보여준다.</summary>
        void ClientInterpolate()
        {
            if (!_clockSynced || _frameCount < 1) return;
            double renderTick = _clientClock - InterpDelayTicks + (Time.time - Time.fixedTime) / Time.fixedDeltaTime;

            SnapshotFrame a = null, b = null;
            for (int i = 0; i < _frameCount; i++)
            {
                var f = _frames[(_frameHead - 1 - i + _frames.Length) % _frames.Length];
                if (f.Tick <= renderTick && (a == null || f.Tick > a.Tick)) a = f;
                if (f.Tick >= renderTick && (b == null || f.Tick < b.Tick)) b = f;
            }
            if (a == null) a = b;
            if (b == null) b = a;
            if (a == null) return;
            float t = b.Tick == a.Tick ? 0f : Mathf.Clamp01((float)((renderTick - a.Tick) / (b.Tick - a.Tick)));

            for (int p = 0; p < MaxPlayers; p++)
            {
                if (p == HumanIndex) continue;
                var pa = a.Players[p];
                var pb = b.Players[p];
                bool teleported = pa.TeleportSeq != pb.TeleportSeq;
                Vector3 pos = teleported ? pb.Pos : Vector3.Lerp(pa.Pos, pb.Pos, t);
                float yaw = teleported ? pb.Yaw : Mathf.LerpAngle(pa.Yaw, pb.Yaw, t);
                float pitch = Mathf.Lerp(pa.Pitch, pb.Pitch, t);
                float crouch = Mathf.Lerp(pa.Crouch, pb.Crouch, t);
                Avatars[p].SetInterpolatedPose(pos, yaw, pitch, crouch);
            }
        }

        // ==================================================================
        // 화면·기타
        // ==================================================================

        void LockCursor()
        {
            if (HumanIndex < 0 || _human.Blocked || HumanIsBot) return;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        /// <summary>준비 단계: 투표 라운드면 커서를 풀어 후보 카드를 누르게, 아니면 잠근 채로.</summary>
        void SelectCursor()
        {
            if (HumanIndex < 0 || _human.Blocked || HumanIsBot) return;
            if (RoundRules.IsVoteRound(Round.RoundNumber)) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            else LockCursor();
        }

        /// <summary>화면(HUD)에서 누른 왕 희망·투표·카드를 다음 명령에 싣는다.</summary>
        public void RequestPick(PlayerClass c) { if (HumanIndex >= 0) _pendingPick[HumanIndex] = (sbyte)c; }
        public void RequestVote(int slot) { if (HumanIndex >= 0) _pendingVote[HumanIndex] = slot < 0 ? VoteCancel : (sbyte)slot; }
        public void RequestCard(int handSlot) { if (HumanIndex >= 0 && handSlot >= 0 && handSlot < WizardRules.HandSize) _pendingCard[HumanIndex] = (sbyte)handSlot; }
        public void RequestResurrect() { if (HumanIndex >= 0) _pendingResurrect[HumanIndex] = true; }

        public void ShowMessage(string msg)
        {
            LastSummonMessage = msg;
            LastSummonMessageUntil = Time.time + 2f;
        }

        public int NearestEnemy(int me, out float dist)
        {
            var a = Avatars[me];
            int best = -1;
            dist = float.MaxValue;
            for (int p = 0; p < MaxPlayers; p++)
            {
                var o = Avatars[p];
                if (o.Team == a.Team || !o.Alive) continue;
                float d = (o.transform.position - a.transform.position).sqrMagnitude;
                if (d < dist) { dist = d; best = p; }
            }
            dist = best >= 0 ? Mathf.Sqrt(dist) : float.MaxValue;
            return best;
        }

        public int NearestEnemyMonster(int me, out Vector3 pos, out float dist)
        {
            pos = Vector3.zero;
            dist = float.MaxValue;
            var a = Avatars[me];
            int enemy = 1 - a.Team;
            int px = LaneX(a.transform.position.x);
            var lane = Monsters.Lane;
            if (px < -2000 || px > lane.LengthMm + 2000) return -1;
            int pz = (int)(a.transform.position.z * 1000f);

            // 퍼펫(클라)에서도 동작하도록 전체 슬롯을 훑는다 (몬스터 수백 마리 수준이면 충분히 빠름)
            int best = -1;
            long bestD = long.MaxValue;
            for (int m = 0; m < Monsters.Capacity; m++)
            {
                if (Monsters.Alive[m] == 0 || Monsters.Team[m] != enemy) continue;
                long dx = Monsters.Pos[m] - px;
                if (dx > 6000 || dx < -6000) continue;
                long dz = Monsters.Lat[m] - pz;
                long d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = m; }
            }
            if (best < 0) return -1;
            pos = new Vector3(WorldX(Monsters.Pos[best]), 0f, Monsters.Lat[best] / 1000f);
            dist = Mathf.Sqrt(bestD) / 1000f;
            return best;
        }

        void CreateCamera()
        {
            var camGo = new GameObject("PlayerCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.03f;
            cam.fieldOfView = GameSettings.Fov;
            GameSettings.Changed += () => { if (cam != null) cam.fieldOfView = GameSettings.Fov; };
            cam.farClipPlane = 600f;
            camGo.AddComponent<AudioListener>();
            camGo.tag = "MainCamera";
            HumanCamera = cam;
            if (HumanIndex >= 0)
            {
                var h = Human;
                camGo.transform.SetParent(h.Eye, false);
                if (h.Body != null) h.Body.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                if (h.WizardHat != null) h.WizardHat.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                if (h.Model != null) h.Model.SetShadowsOnly();
            }
            else
            {
                camGo.transform.position = new Vector3(0f, 25f, -30f);
                camGo.transform.LookAt(Vector3.zero);
            }
        }

        /// <summary>눈앞(1인칭) 칼을 새 모델로 바꾼다. 위치·회전은 기존 칼 자리 그대로.</summary>
        static void ReplaceEyeKnife(PlayerAvatar a, Transform k)
        {
            var old = a.Knife;
            k.name = "Knife";
            foreach (var col in k.GetComponentsInChildren<Collider>(true)) Destroy(col);
            foreach (var r in k.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (old != null)
            {
                k.localPosition = old.localPosition;
                k.localRotation = old.localRotation;
                Destroy(old.gameObject);
            }
            a.Knife = k;
        }

        /// <summary>외형 설정(SkinSet) 적용. 서버는 부르지 않는다. 판정·충돌은 건드리지 않음.</summary>
        void ApplySkin(SkinSet skin, int humanSlot)
        {
            if (skin == null)
            {
                // 스킨이 없으면 코드로 만든 기사·왕 + 1인칭 칼
                for (int p = 0; p < MaxPlayers; p++)
                {
                    var a = Avatars[p];
                    a.Model = AvatarModel.AttachProcedural(a);
                    ReplaceEyeKnife(a, ProceduralParts.Knife("Knife", a.Eye));
                    if (p != humanSlot) a.HideEyeKnife = true;   // 남은 손에 든 칼로 보임
                    a.RefreshKnife();
                }
                return;
            }
            for (int p = 0; p < MaxPlayers; p++)
            {
                var a = Avatars[p];
                if (skin.KnifeView != null && a.Knife != null) ReplaceEyeKnife(a, Instantiate(skin.KnifeView, a.Eye, false).transform);
                else if (a.Knife != null) ReplaceEyeKnife(a, ProceduralParts.Knife("Knife", a.Eye));
                a.Model = AvatarModel.Attach(a, skin);
                if (a.Model != null && (skin.KnifeInHand != null || a.Model.IsProcedural) && p != humanSlot) a.HideEyeKnife = true;
                a.RefreshKnife();
            }
            if (skin.Environment != null) Instantiate(skin.Environment).name = "Environment (Skin)";
            if (skin.HideDefaultWorld)
            {
                var world = GameObject.Find("World");
                if (world != null)
                    foreach (var r in world.GetComponentsInChildren<Renderer>(true)) r.enabled = false;   // 충돌체는 그대로
            }
        }

        void UpdateSpectatorCamera(float dt)
        {
            if (HumanCamera == null || HumanIndex < 0) return;
            var h = Human;
            if (h.Alive || Round.Phase != RoundPhase.Live)
            {
                if (HumanCamera.transform.parent != h.Eye)
                {
                    HumanCamera.transform.SetParent(h.Eye, false);
                    HumanCamera.transform.localPosition = Vector3.zero;
                    HumanCamera.transform.localRotation = Quaternion.identity;
                }
                return;
            }

            PlayerAvatar follow = null;
            for (int p = 0; p < MaxPlayers; p++)
                if (Avatars[p].Team == h.Team && Avatars[p].Alive) { follow = Avatars[p]; break; }

            if (HumanCamera.transform.parent != null) HumanCamera.transform.SetParent(null, true);
            Vector3 target;
            Quaternion look;
            if (follow != null)
            {
                Vector3 fwd = Quaternion.Euler(0f, follow.Yaw, 0f) * Vector3.forward;
                Vector3 fp = follow.Visual != null ? follow.Visual.position : follow.transform.position;
                target = fp + Vector3.up * 2.6f - fwd * 4f;
                look = Quaternion.LookRotation(fp + Vector3.up * 1.2f - target);
            }
            else
            {
                target = new Vector3(0f, 25f, -30f);
                look = Quaternion.LookRotation(Vector3.zero - target);
            }
            HumanCamera.transform.position = Vector3.Lerp(HumanCamera.transform.position, target, dt * 6f);
            HumanCamera.transform.rotation = Quaternion.Slerp(HumanCamera.transform.rotation, look, dt * 6f);
        }

        PlayerAvatar CreateAvatar(int index, byte team)
        {
            var go = new GameObject($"Player {index}");
            go.layer = PlayerLayer;
            go.SetActive(false);
            var a = go.AddComponent<PlayerAvatar>();
            a.Index = index;
            a.Team = team;
            a.DisplayName = BotName(index);

            var mat = team == 0 ? BlueMat : RedMat;
            var visual = new GameObject("Visual").transform;
            visual.SetParent(go.transform, false);
            a.Visual = visual;

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(visual, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
            var br = body.GetComponent<Renderer>();
            if (mat != null) br.sharedMaterial = mat;
            a.Body = br;

            var hat = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            hat.name = "WizardHat";
            Destroy(hat.GetComponent<Collider>());
            hat.transform.SetParent(visual, false);
            hat.transform.localPosition = new Vector3(0f, 1.95f, 0f);
            hat.transform.localScale = new Vector3(0.55f, 0.2f, 0.55f);
            if (HatMat != null) hat.GetComponent<Renderer>().sharedMaterial = HatMat;
            hat.SetActive(false);
            a.WizardHat = hat;

            var eye = new GameObject("Eye").transform;
            eye.SetParent(visual, false);
            eye.localPosition = new Vector3(0f, PlayerBody.StandEyeHeight, 0f);
            a.Eye = eye;

            var knife = GameObject.CreatePrimitive(PrimitiveType.Cube);
            knife.name = "Knife";
            Destroy(knife.GetComponent<Collider>());
            knife.transform.SetParent(eye, false);
            knife.transform.localScale = new Vector3(0.03f, 0.05f, 0.32f);
            knife.transform.localPosition = new Vector3(0.28f, -0.25f, 0.45f);
            if (KnifeMat != null) knife.GetComponent<Renderer>().sharedMaterial = KnifeMat;
            knife.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            a.Knife = knife.transform;

            go.SetActive(true);
            a.SetClass(PlayerClass.Knight);
            return a;
        }
    }

    public struct KillFeedEntry
    {
        public string Killer;
        public byte KillerTeam;
        public string Victim;
        public byte VictimTeam;
        public string How;
        public float Time;
    }
}
