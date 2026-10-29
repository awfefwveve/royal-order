using System;
using KnifeWar.Core;
using Unity.Collections;
using Unity.Mathematics;
public static class Program2 {
  static int fail = 0;
  static void Check(bool ok, string msg) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + msg); if (!ok) fail++; }
  const int TR = 128;

  static KnifeCombat Setup() {
    var c = new KnifeCombat(2, TR);
    c.Teams[0] = 0; c.Teams[1] = 1; c.CanAttack[0] = 1; c.CanAttack[1] = 1;
    c.ResetForRound();
    return c;
  }
  static void Place(KnifeCombat c, int p, float x, float z, float yaw) => c.Poses[p] = new PlayerPose { Root = new float3(x, 0, z), Yaw = yaw, Alive = 1 };
  static void Run(KnifeCombat c, int n, Action<int> perTick = null) { for (int i = 0; i < n; i++) { perTick?.Invoke(c.Tick); c.Step(); Collect(c); c.EndTick(); } }
  static System.Collections.Generic.List<KnifeHitEvent> events = new System.Collections.Generic.List<KnifeHitEvent>();
  static void Collect(KnifeCombat c) { for (int i = 0; i < c.Events.Length; i++) events.Add(c.Events[i]); }

  public static int Main() {
    Console.WriteLine("[칼 판정]");
    // 1. 정면 머리
    { var c = Setup(); events.Clear(); Place(c, 0, 0, 0, 0); Place(c, 1, 0, 1.2f, MathF.PI); Run(c, 20);
      c.SubmitInput(0, new KnifeInput { Pressed = KnifeAttack.Slash, Yaw = 0, Pitch = 0, ViewTick = c.Tick - 13 });
      int pressTick = c.Tick; Run(c, 12);
      Check(events.Count == 1 && events[0].Part == HitPart.Head && events[0].Damage == 80, $"정면 베기 → 머리 80 (got {(events.Count>0? events[0].Part+" "+events[0].Damage : "miss")})");
      Check(events.Count == 1 && c.Hp[1] == 20, "체력 100 → 20");
    }
    // 2. 아래 조준 → 몸통
    { var c = Setup(); events.Clear(); Place(c, 0, 0, 0, 0); Place(c, 1, 0, 1.2f, MathF.PI); Run(c, 20);
      c.SubmitInput(0, new KnifeInput { Pressed = KnifeAttack.Slash, Yaw = 0, Pitch = -0.35f, ViewTick = c.Tick - 13 }); Run(c, 12);
      Check(events.Count == 1 && (events[0].Part == HitPart.Chest || events[0].Part == HitPart.Stomach), $"아래 조준 → 몸통 (got {(events.Count>0? events[0].Part.ToString() : "miss")})");
    }
    // 3. 사거리 밖
    { var c = Setup(); events.Clear(); Place(c, 0, 0, 0, 0); Place(c, 1, 0, 2.6f, MathF.PI); Run(c, 20);
      c.SubmitInput(0, new KnifeInput { Pressed = KnifeAttack.Slash, Yaw = 0, Pitch = -0.2f, ViewTick = c.Tick - 13 }); Run(c, 12);
      Check(events.Count == 0, "2.6m 거리 → 빗나감");
    }
    // 4. 옆으로 빗나감
    { var c = Setup(); events.Clear(); Place(c, 0, 0, 0, 0); Place(c, 1, 0, 1.2f, MathF.PI); Run(c, 20);
      c.SubmitInput(0, new KnifeInput { Pressed = KnifeAttack.Slash, Yaw = 0.9f, Pitch = 0, ViewTick = c.Tick - 13 }); Run(c, 12);
      Check(events.Count == 0, "옆(약 50도) 조준 → 빗나감");
    }
    // 5. 랙 보정: 상대가 빠르게 옆으로 지나감. 클라 화면(100ms 전)엔 정면에 있었음
    {
      Func<bool, bool> trial = (rewind) => {
        var c = Setup(); events.Clear(); Place(c, 0, 0, 0, 0); Place(c, 1, -3f, 1.1f, MathF.PI);
        // 초속 6m로 x+ 방향 이동, tick 84에 x=0 통과
        Action<int> move = t => { float x = -3f + (t - 20) * (6f / TR); Place(c, 1, Math.Min(x, 20f), 1.1f, MathF.PI); };
        Run(c, 84 + 13, move); // 지금 tick 97, 클라 화면(100ms 전) = tick 84 (x=0)
        // 윈드업 8틱 동안 클라 화면도 진행 → 판정 시점 화면 = tick 48 (x≈0.75): 베기 반경 안
        c.SubmitInput(0, new KnifeInput { Pressed = KnifeAttack.Slash, Yaw = 0, Pitch = -0.2f, ViewTick = rewind ? 84 - 8 : c.Tick });
        Run(c, 12, move);
        return events.Count == 1;
      };
      Check(trial(true), "랙 보정 ON: 화면에서 맞춘 칼은 들어감 (상대는 서버상 이미 지나감)");
      Check(!trial(false), "랙 보정 OFF: 같은 칼이 빗나감 (보정이 실제로 일하는지 확인)");
    }
    // 6. 되감기 한도 (250ms 넘는 옛날 화면은 한도로 고정)
    { var c = Setup(); events.Clear(); Place(c, 0, 0, 0, 0); Place(c, 1, 0, 1.2f, MathF.PI); Run(c, 100);
      Place(c, 1, 5f, 1.2f, MathF.PI); Run(c, 60); // 상대가 0.47초 전에 떠남
      c.SubmitInput(0, new KnifeInput { Pressed = KnifeAttack.Slash, Yaw = 0, Pitch = 0, ViewTick = c.Tick - 120 }); Run(c, 12);
      Check(events.Count == 0, "0.9초 전 화면 요청 → 한도(250ms)로 잘려서 빗나감 (핵 방지)");
    }
    // 7. 백스탭 즉사
    { var c = Setup(); events.Clear(); Place(c, 0, 0, 0, 0); Place(c, 1, 0, 1.0f, 0f); Run(c, 20);
      c.SubmitInput(0, new KnifeInput { Pressed = KnifeAttack.Stab, Yaw = 0, Pitch = -0.4f, ViewTick = c.Tick - 13 }); Run(c, 40);
      Check(events.Count == 1 && events[0].FromBehind && events[0].Kill, $"등 뒤 찌르기 → 즉사 (got {(events.Count>0? events[0].Damage+" behind="+events[0].FromBehind : "miss")})");
      Check(c.Poses[1].Alive == 0, "죽은 플레이어 Alive=0");
    }
    // 8. 쿨다운
    { var c = Setup(); events.Clear(); Place(c, 0, 0, 0, 0); Place(c, 1, 0, 1.2f, MathF.PI); Run(c, 20);
      c.SubmitInput(0, new KnifeInput { Pressed = KnifeAttack.Slash, Yaw = 0, Pitch = -0.3f, ViewTick = c.Tick - 13 }); Run(c, 10);
      c.SubmitInput(0, new KnifeInput { Pressed = KnifeAttack.Slash, Yaw = 0, Pitch = -0.3f, ViewTick = c.Tick - 13 }); Run(c, 10);
      Check(events.Count == 1, "회복 시간 중 연타는 무시");
      Run(c, 60);
      c.SubmitInput(0, new KnifeInput { Pressed = KnifeAttack.Slash, Yaw = 0, Pitch = -0.3f, ViewTick = c.Tick - 13 }); Run(c, 12);
      Check(events.Count == 2, "회복 후 다음 공격 정상");
    }
    // 9. 같은 팀은 안 맞음
    { var c = Setup(); c.Teams[1] = 0; events.Clear(); Place(c, 0, 0, 0, 0); Place(c, 1, 0, 1.2f, MathF.PI); Run(c, 20);
      c.SubmitInput(0, new KnifeInput { Pressed = KnifeAttack.Slash, Yaw = 0, Pitch = 0, ViewTick = c.Tick - 13 }); Run(c, 12);
      Check(events.Count == 0, "아군 판정 없음");
    }
    // 10. 앉은 상대 머리 위로 휘두르면 빗나감
    { var c = Setup(); events.Clear(); Place(c, 0, 0, 0, 0); var p = new PlayerPose{ Root=new float3(0,0,1.2f), Yaw=MathF.PI, Crouch=1, Alive=1}; c.Poses[1]=p; Run(c, 20);
      c.SubmitInput(0, new KnifeInput { Pressed = KnifeAttack.Slash, Yaw = 0, Pitch = 0.15f, ViewTick = c.Tick - 13 }); Run(c, 12);
      Check(events.Count == 0, "앉은 상대 머리 위 조준 → 빗나감");
    }

    Console.WriteLine("[라운드]");
    {
      var r = new RoundRules(RoundConfig.Default, TR);
      for (int i = 0; i < 12; i++) r.SetConnected(i, true);
      int t = 0; r.Tick(t++);
      Check(r.Phase == RoundPhase.Select && r.RoundNumber == 1, "12명 접속 → 1라운드 준비 단계");
      Check(r.PickClass(2, PlayerClass.Wizard) && r.PickClass(3, PlayerClass.Wizard), "준비 단계에서 마법사 선택");
      Check(r.GetPick(2) == PlayerClass.Wizard, "팀원에게 보일 선택값");
      while (r.Phase == RoundPhase.Select) r.Tick(t++);
      Check(r.Phase == RoundPhase.Live && r.Class[2] == PlayerClass.Wizard && r.Class[0] == PlayerClass.Knight, "라운드 시작 시 직업 확정");
      Check(!r.PickClass(0, PlayerClass.Wizard), "라운드 중 직업 변경 불가");
      for (int i = 6; i < 12; i++) r.Kill(i);
      r.Tick(t++);
      Check(r.Phase == RoundPhase.RoundEnd && r.Score[0] == 1, "상대 전멸 → 팀0 득점");
      while (r.Phase == RoundPhase.RoundEnd) r.Tick(t++);
      Check(r.Phase == RoundPhase.Select && r.RoundNumber == 2 && r.Class[2] == PlayerClass.Wizard, "2라운드: 안 고르면 이전 직업 유지");
      while (r.Phase == RoundPhase.Select) r.Tick(t++);
      Check(r.AliveCount(1) == 6, "새 라운드 전원 부활");
      r.SetConnected(7, false);
      while (r.Phase == RoundPhase.Live) r.Tick(t++);
      Check(r.Score[0] == 2, "시간 초과 → 생존자 많은 팀 득점 (나간 유저는 사망 처리)");
    }

    Console.WriteLine("[마법사]");
    {
      var r = new RoundRules(RoundConfig.Default, TR);
      for (int i = 0; i < 12; i++) r.SetConnected(i, true);
      int t = 0; r.Tick(t++); r.PickClass(0, PlayerClass.Wizard); r.PickClass(1, PlayerClass.Wizard);
      while (r.Phase == RoundPhase.Select) r.Tick(t++);
      var sim = new MonsterSim(1000, TR, LaneConfig.Default);
      var w = new WizardRules(12, TR, teamMonsterCap: 5);
      w.ResetForRound(30);
      sim.PrepareTick();
      Check(w.TrySummon(0, 0, r, sim) == WizardRules.SummonResult.Ok, "마나 30 → 졸병(10) 소환");
      Check(w.TrySummon(0, 1, r, sim) == WizardRules.SummonResult.NoMana, "남은 20 → 탱커(30) 마나 부족");
      Check(w.TrySummon(2, 0, r, sim) == WizardRules.SummonResult.NotWizard, "기사는 소환 불가");
      for (int i = 0; i < TR * 3; i++) w.Tick(i, r);
      Check(Math.Abs(w.Mana(0) - 56f) < 0.6f, $"3초 회복 (+36) → 약 56 (got {w.Mana(0):F1})");
      int ok = 1; while (w.TrySummon(1, 0, r, sim) == WizardRules.SummonResult.Ok) ok++;
      var last = w.TrySummon(0, 0, r, sim);
      Check(sim.AliveInTeam(0) == 5 && last == WizardRules.SummonResult.TeamCap, $"팀 상한 5 공유 → 두 마법사 합쳐 5마리에서 거부 (got {sim.AliveInTeam(0)}, {last})");
      Check(w.TryBuff(0, 1, 5, 20, t, r) && w.TeamBuffs[0].IsActive(t) && !w.TeamBuffs[0].IsActive(t + 5 * TR), "팀 버프 5초 지속");
    }

    Console.WriteLine(fail == 0 ? "ALL OK" : $"FAILURES: {fail}");
    return fail;
  }
}
