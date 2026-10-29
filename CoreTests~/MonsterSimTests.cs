using System;
using KnifeWar.Core;
using Unity.Collections;
using Unity.Mathematics;
public static class Program {
  public static int Main() {
    int fail = 0;
    var sim = new MonsterSim(5000, 128, LaneConfig.Default);
    int serial = 0; int target = 2000;
    int totalDeaths = 0, totalLeaks = 0, hpEv = 0;
    for (int t = 0; t < 128 * 60; t++) {
      sim.PrepareTick();
      for (byte team = 0; team < 2; team++) {
        int want = Math.Min(target/2 - sim.AliveInTeam(team), 64);
        for (int i = 0; i < want; i++) sim.TrySpawn(team, (byte)(serial++ % 3));
      }
      sim.Step();
      totalDeaths += sim.Counters[SimCounter.DeathEvents];
      totalLeaks += sim.Counters[SimCounter.LeaksTeam0] + sim.Counters[SimCounter.LeaksTeam1];
      hpEv += sim.Counters[SimCounter.HpEvents];
      // invariant: alive count matches flags
      if (t % 512 == 0) {
        int a = 0; for (int m = 0; m < sim.Capacity; m++) a += sim.Alive[m];
        if (a != sim.AliveCount) { Console.WriteLine($"ALIVE MISMATCH t={t} flags={a} count={sim.AliveCount}"); fail++; }
        if (a + sim.FreeSlots != sim.Capacity) { Console.WriteLine($"SLOT LEAK t={t}"); fail++; }
      }
    }
    Console.WriteLine($"60s: alive={sim.AliveCount} deaths={totalDeaths} leaks={totalLeaks} hpEvents={hpEv}");
    if (totalDeaths == 0 || hpEv == 0) { Console.WriteLine("NO COMBAT"); fail++; }

    // frontline position: find min/max pos of team0
    int mn=int.MaxValue,mx=0; for (int m=0;m<sim.Capacity;m++) if (sim.Alive[m]==1&&sim.Team[m]==0){mn=Math.Min(mn,sim.Pos[m]);mx=Math.Max(mx,sim.Pos[m]);}
    Console.WriteLine($"team0 pos range: {mn}..{mx} mm");

    // melee query: place query on a known team1 monster's current position
    int victim=-1; for (int m=0;m<sim.Capacity;m++) if (sim.Alive[m]==1&&sim.Team[m]==1&&sim.SpawnTick[m]<sim.Tick-20){victim=m;break;}
    var q = new NativeArray<MeleeQuery>(2, Allocator.Persistent); var r = new NativeArray<int>(2, Allocator.Persistent);
    q[0] = new MeleeQuery{ Origin=new float2(sim.Pos[victim]-1000, sim.Lat[victim]), Dir=new float2(1,0), RangeMm=1800, RadiusMm=250, RewindTick=sim.Tick, AttackerTeam=0 };
    q[1] = new MeleeQuery{ Origin=new float2(sim.Pos[victim]-1000, 6000), Dir=new float2(1,0), RangeMm=1800, RadiusMm=250, RewindTick=sim.Tick, AttackerTeam=0 };
    sim.QueryMelee(q, r);
    Console.WriteLine($"melee hit={r[0]} (victim {victim}) miss-case={r[1]}");
    if (r[0] < 0) { Console.WriteLine("MELEE SHOULD HIT"); fail++; }
    if (r[1] >= 0) { Console.WriteLine("MELEE SHOULD MISS (3m sideways)"); fail++; }

    // rewind: history 13 ticks ago should equal recorded pos
    int rt = sim.Tick - 13; var h = sim.History[(rt % sim.HistoryLength) * sim.Capacity + victim];
    Console.WriteLine($"victim now={sim.Pos[victim]} 13 ticks ago={h.x} state={sim.State[victim]}");

    // determinism: rerun same scenario, compare checksum
    long Run() { var s2 = new MonsterSim(5000,128,LaneConfig.Default); int ser=0; for(int t=0;t<128*20;t++){ s2.PrepareTick(); for(byte tm=0;tm<2;tm++){int w=Math.Min(1000-s2.AliveInTeam(tm),64); for(int i=0;i<w;i++) s2.TrySpawn(tm,(byte)(ser++%3));} s2.Step(); } long c=0; for(int m=0;m<s2.Capacity;m++) c = c*31 + s2.Pos[m] + s2.Hp[m]*7 + s2.Alive[m]; return c; }
    long c1 = Run(), c2 = Run();
    Console.WriteLine($"determinism {(c1==c2?"OK":"FAIL")}"); if (c1!=c2) fail++;
    Console.WriteLine(fail==0 ? "ALL OK" : $"FAILURES: {fail}");
    return fail;
  }
}
