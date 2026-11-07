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

    Console.WriteLine("[라운드 · 왕 투표]");
    {
      var r = new RoundRules(RoundConfig.Default, TR, seed: 7);
      for (int i = 0; i < 12; i++) r.SetConnected(i, true);
      int t = 0; r.Tick(t++);
      Check(r.Phase == RoundPhase.Select && r.RoundNumber == 1 && r.VoteRoundNow, "12명 접속 → 1라운드 준비 단계 = 왕 투표");
      Check(r.PickClass(2, PlayerClass.King) && r.PickClass(3, PlayerClass.King), "같은 팀 둘 다 왕 희망 가능 (후보)");
      Check(!r.Vote(0, 4), "후보가 아닌 팀원에게는 투표 불가");
      Check(!r.Vote(0, 8), "상대 팀에게는 투표 불가");
      Check(r.Vote(0, 2) && r.Vote(1, 2) && r.Vote(4, 3) && r.VoteCount(2) == 2 && r.VoteCount(3) == 1, "투표 집계");
      Check(r.Vote(1, 3) && r.VoteCount(2) == 1 && r.VoteCount(3) == 2, "다시 누르면 표 바꿈");
      Check(r.PickClass(8, PlayerClass.King), "상대 팀 희망자");
      Check(r.GetPick(2) == PlayerClass.King && r.IsCandidate(2), "팀원에게 보일 후보 표시");
      while (r.Phase == RoundPhase.Select) r.Tick(t++);
      Check(r.Phase == RoundPhase.Live && r.Class[3] == PlayerClass.King && r.Class[2] == PlayerClass.Knight && r.Class[8] == PlayerClass.King, "최다 득표가 왕 (3번 2표, 2번 1표)");
      Check(r.KingOf(0) == 3 && r.KingOf(1) == 8, "팀별 왕 한 명씩");
      Check(!r.PickClass(0, PlayerClass.King) && !r.Vote(0, 3), "라운드 중 희망·투표 불가");
      for (int i = 6; i < 12; i++) r.Kill(i);
      r.Tick(t++);
      Check(r.Phase == RoundPhase.RoundEnd && r.Score[0] == 1, "상대 전멸 → 팀0 득점");
      while (r.Phase == RoundPhase.RoundEnd) r.Tick(t++);
      Check(r.Phase == RoundPhase.Select && r.RoundNumber == 2 && !r.VoteRoundNow && r.NextVoteRound == 4, "2라운드: 투표 없음, 다음 투표는 4라운드");
      Check(!r.PickClass(5, PlayerClass.King), "투표 없는 라운드엔 희망 불가");
      while (r.Phase == RoundPhase.Select) r.Tick(t++);
      Check(r.Class[3] == PlayerClass.King && r.Class[8] == PlayerClass.King, "2라운드: 왕 유지");
      Check(r.AliveCount(1) == 6, "새 라운드 전원 부활");
      r.SetConnected(7, false);
      while (r.Phase == RoundPhase.Live) r.Tick(t++);
      Check(r.Score[0] == 2, "시간 초과 → 생존자 많은 팀 득점 (나간 유저는 사망 처리)");
    }

    Console.WriteLine("[왕 투표 규칙]");
    {
      // 1라운드 무표 → 희망자 중 무작위
      var r = new RoundRules(RoundConfig.Default, TR, seed: 3);
      for (int i = 0; i < 12; i++) r.SetConnected(i, true);
      int t = 0; r.Tick(t++);
      r.PickClass(1, PlayerClass.King); r.PickClass(4, PlayerClass.King);
      while (r.Phase == RoundPhase.Select) r.Tick(t++);
      int k0 = r.KingOf(0), k1 = r.KingOf(1);
      Check((k0 == 1 || k0 == 4) && k1 >= 6 && k1 < 12, $"무표: 희망자 중 무작위 / 희망자 없는 팀은 팀원 중 무작위 (got {k0}, {k1})");

      // 1라운드 동점 → 무작위 (후보 둘 중)
      var r2 = new RoundRules(RoundConfig.Default, TR, seed: 11);
      for (int i = 0; i < 12; i++) r2.SetConnected(i, true);
      t = 0; r2.Tick(t++);
      r2.PickClass(0, PlayerClass.King); r2.PickClass(1, PlayerClass.King); r2.Vote(2, 0); r2.Vote(3, 1);
      while (r2.Phase == RoundPhase.Select) r2.Tick(t++);
      Check(r2.KingOf(0) == 0 || r2.KingOf(0) == 1, "동점: 후보 중 무작위");

      // 4라운드 재투표: 무표면 이전 왕 유지, 표가 있으면 바뀜
      var r3 = new RoundRules(RoundConfig.Default, TR, seed: 5);
      for (int i = 0; i < 12; i++) r3.SetConnected(i, true);
      t = 0; r3.Tick(t++);
      r3.PickClass(2, PlayerClass.King); r3.Vote(0, 2); r3.PickClass(9, PlayerClass.King); r3.Vote(6, 9);
      while (r3.Phase == RoundPhase.Select) r3.Tick(t++);
      Check(r3.KingOf(0) == 2 && r3.KingOf(1) == 9, "1라운드 왕 확정");
      for (int round = 1; round <= 3; round++)
      {
        for (int i = 6; i < 12; i++) r3.Kill(i);
        r3.Tick(t++);
        while (r3.Phase != RoundPhase.Select) r3.Tick(t++);
        if (round < 3) { Check(!r3.VoteRoundNow, $"{round + 1}라운드는 투표 없음"); while (r3.Phase == RoundPhase.Select) r3.Tick(t++); }
      }
      Check(r3.RoundNumber == 4 && r3.VoteRoundNow, "4라운드 = 재투표");
      r3.PickClass(5, PlayerClass.King);   // 팀0: 새 희망자만 있고 아무도 안 찍음
      r3.PickClass(7, PlayerClass.King); r3.Vote(8, 7); r3.Vote(10, 7);   // 팀1: 7번이 2표
      while (r3.Phase == RoundPhase.Select) r3.Tick(t++);
      Check(r3.KingOf(0) == 2, "재투표 무표 → 이전 왕 유지");
      Check(r3.KingOf(1) == 7 && r3.Class[9] == PlayerClass.Knight, "재투표 득표 → 새 왕, 이전 왕은 기사로");

      // 왕이 나가면 다음 라운드에 무작위 새 왕
      for (int i = 6; i < 12; i++) r3.Kill(i);
      r3.Tick(t++);
      r3.SetConnected(2, false);
      while (r3.Phase != RoundPhase.Live) r3.Tick(t++);
      Check(r3.KingOf(0) >= 0 && r3.KingOf(0) != 2, "왕이 나가면 라운드 시작 때 다른 팀원이 왕");
    }

    Console.WriteLine("[왕 사망 = 라운드 패배]");
    {
      var r = new RoundRules(RoundConfig.Default, TR);
      for (int i = 0; i < 12; i++) r.SetConnected(i, true);
      int t = 0; r.Tick(t++); r.PickClass(5, PlayerClass.King); r.PickClass(11, PlayerClass.King);
      while (r.Phase == RoundPhase.Select) r.Tick(t++);
      r.Kill(11); r.Tick(t++);
      Check(r.Phase == RoundPhase.RoundEnd && r.Score[0] == 1 && r.AliveCount(1) == 5, "상대 왕만 죽어도 즉시 라운드 승리 (기사 5명 생존)");
      while (r.Phase != RoundPhase.Live) r.Tick(t++);
      r.Kill(0); r.Kill(1); r.Tick(t++);
      Check(r.Phase == RoundPhase.Live, "기사가 죽는 건 라운드 계속");
    }

    Console.WriteLine("[몬스터는 적 왕에게]");
    {
      var lane = LaneConfig.Default;
      var sim = new MonsterSim(64, TR, lane);
      sim.PrepareTick();
      sim.TrySpawn(0, 0);
      int kingPos = lane.LengthMm - 20_000, kingLat = lane.HalfWidthMm;   // 적 왕: 끝에서 20m 앞, 다리 가장자리
      for (int i = 0; i < TR * 60; i++)
      {
        sim.PrepareTick();
        sim.SetPlayer(0, kingPos, kingLat, 1, true, king: true);
        sim.Step();
      }
      int m = -1; for (int i = 0; i < 64; i++) if (sim.Alive[i] != 0) m = i;
      Check(m >= 0 && Math.Abs(sim.Pos[m] - kingPos) < 2_000 && Math.Abs(sim.Lat[m] - kingLat) < 1_000, $"왕 앞까지 가서 좌우도 붙음 (pos {(m >= 0 ? sim.Pos[m] : -1)}, lat {(m >= 0 ? sim.Lat[m] : -1)})");
      Check(sim.PlayerDamage[0] > 0 || sim.TargetPlayer[m] == 0, "왕을 공격");
      sim.Dispose();

      var sim2 = new MonsterSim(64, TR, lane);
      sim2.PrepareTick(); sim2.TrySpawn(0, 0);
      for (int i = 0; i < TR * 80; i++) { sim2.PrepareTick(); sim2.Step(); }
      int m2 = -1; for (int i = 0; i < 64; i++) if (sim2.Alive[i] != 0) m2 = i;
      Check(m2 >= 0 && sim2.Pos[m2] == lane.LengthMm, "왕이 없으면 적 진영 끝까지 가서 버팀 (사라지지 않음)");
      sim2.Dispose();
    }

    Console.WriteLine("[왕]");
    {
      var r = new RoundRules(RoundConfig.Default, TR);
      for (int i = 0; i < 12; i++) r.SetConnected(i, true);
      int t = 0; r.Tick(t++); r.PickClass(0, PlayerClass.King); r.PickClass(6, PlayerClass.King);
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
      var last = WizardRules.SummonResult.Ok; while ((last = w.TrySummon(0, 0, r, sim)) == WizardRules.SummonResult.Ok) { }
      Check(sim.AliveInTeam(0) == 5 && last == WizardRules.SummonResult.TeamCap, $"팀 상한 5 → 5마리에서 거부 (got {sim.AliveInTeam(0)}, {last})");
      Check(w.TryBuff(0, 1, 5, 10, t, r) && w.TeamBuffs[0].IsActive(t) && !w.TeamBuffs[0].IsActive(t + 5 * TR), "팀 버프 5초 지속");
    }

    Console.WriteLine(fail == 0 ? "ALL OK" : $"FAILURES: {fail}");
    return fail;
  }
}
