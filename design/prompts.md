# ROYAL ORDER 에셋 생성 프롬프트 (v1)

상위 기준: [`art-direction.md`](art-direction.md) · 파일 규격: [`asset-spec.md`](asset-spec.md)
캐릭터 얼굴·갑옷·문양은 **아직 확정 전**이라 아래 디자인은 제안안이다. 마음에 드는 결과가 나오면 그 이미지를 기준으로 `design/characters/이름.md` 에 확정한다.
프롬프트는 영어가 결과가 더 안정적이라 영어로 썼다 (붙여넣기용). 한글은 설명.

---

## 0. 파이프라인과 도구

| 단계 | 도구 | 결과 |
|---|---|---|
| 1. 원화·턴어라운드 | ChatGPT 이미지 (GPT Image) | ① **정면 A포즈 한 장** (3D 생성용) ② **턴어라운드 시트** (확인·기록용) |
| 2. 3D 모델 | VARCO **3D 생성** (이미지 → 3D) | FBX |
| 3. 리깅 | VARCO **애니메이션 스튜디오** (휴머노이드 자동 리깅) | 뼈대 들어간 FBX |
| 4. 동작 | 영상 생성 AI → DINO 추적 → 뼈대에 입히기 | 동작 클립 |
| 5. 소리 | VARCO **Sound** | WAV |

**VARCO 플로우를 쓰는 곳:** 1단계를 GPT에서 하니까, 플로우는 **"이미지 넣기 → 3D 생성 → (리깅)"** 만 연결해 두고 이미지만 바꿔 끼우며 반복하는 용도가 제일 맞다. 몬스터 3종·칼·맵 조각처럼 같은 설정으로 여러 개 뽑을 때 시간이 줄어든다. 처음 한두 개는 메뉴(3D 생성 → 애니메이션 스튜디오)에서 하나씩 해 보고 설정이 정해지면 플로우로 묶는다.

**4단계 주의:** 영상 → DINO 추적은 손·발이 미끄러지거나 칼 각도가 흔들리기 쉽다. 베기·찌르기는 판정 타이밍(아래 표)이 맞아야 하므로, VARCO 애니메이션 스튜디오에 기본 동작(걷기·달리기 등)이 있으면 그걸 먼저 쓰고, 영상 방식은 베기·찌르기·소환처럼 고유한 동작에만 쓰는 걸 권한다.

---

## 1. 공통 스타일 문구 (모든 이미지 프롬프트 끝에 붙임)

```
Style: dark fantasy cinematic realism, AAA console game concept art, physically based materials,
realistic human anatomy, clear readable silhouette, restrained detail (no clutter, no excessive scratches,
no excessive glow), palette: dark stone #17191D, iron #34383E, royal crimson #641E27, antique gold #A67C38,
gold highlight #D9C49A. Soft even studio lighting, key light 5600K, subtle cool rim light.
No text, no logo, no watermark, no UI, no frame.
```

### 팀 색 (제안)
- **A팀(파랑)**: 차가운 강철 청색 `#2F4A66` 망토·천, 은색 금속
- **B팀(빨강)**: 왕실 적색 `#641E27` 망토·천, 고대 금색 금속
- 같은 디자인에 천·장식 색만 바꾼다 → 두 팀 모델을 하나로 만들고 텍스처만 두 벌.

---

## 2. GPT — 캐릭터 원화

### 2-1. 3D 생성용 정면 한 장 (캐릭터마다 이것부터)
```
Full-body character for 3D modeling reference: [CHARACTER].
Strict front view, orthographic camera, neutral A-pose (arms 45 degrees down, palms facing the body,
feet shoulder-width apart, standing straight), entire body visible from head to feet with margin,
plain flat mid-grey background (#808080), even shadowless lighting, sharp focus, 1:1 or 3:4 portrait.
Weapon NOT in hand (the character holds nothing). Cape hangs straight down behind the body.
[공통 스타일 문구]
```

### 2-2. 턴어라운드 시트 (정면이 마음에 들면 그 이미지를 같이 넣고)
```
Character turnaround sheet of the SAME character as the attached image, identical design, proportions,
armor, colors and details in every view. Four views side by side on one wide image, same scale, same height,
aligned on the same ground line: front (0°), right side (90°), back (180°), three-quarter front (45°).
Orthographic camera, neutral A-pose, plain flat mid-grey background, even lighting, no shadows on the ground.
Below: a small close-up of the face/helmet and a material swatch row (steel, gold, leather, cloth).
[공통 스타일 문구]
```

### [CHARACTER] 칸에 넣을 내용

**기사 (Knight)** — 키 180~185cm, 날렵한 중갑
```
a Royal Order knight, male human, 183 cm, 8 heads tall, athletic build. Fitted plate armor with layered pauldrons,
articulated gauntlets and sabatons, a chainmail skirt over leather. Open-face helmet with a narrow nasal guard
so the face is visible: short dark hair, trimmed beard, scarred determined face. A short heraldic tabard and a
knee-length cape in [TEAM CLOTH COLOR], small royal crest (a stylized crown over a sword) on the chest.
Belt with a knife sheath on the right hip. Silhouette: broad shoulders, tapered waist, agile.
```

**왕 (King)** — 키 190cm, 수직적이고 권위적인 실루엣
```
the King of the Royal Order, male human, 190 cm, regal and tall, mature (around 50), grey-streaked beard,
stern face. Ornate gilded plate cuirass under a heavy floor-length royal mantle in [TEAM CLOTH COLOR] with
white fur collar, a tall slender gold crown with a single gem. Long ceremonial robe under the armor.
Holds nothing (hands empty, his power is summoning). Silhouette: vertical, symmetrical, triangular from
crown down to the wide mantle.
```

`[TEAM CLOTH COLOR]` → A팀: `cold steel blue (#2F4A66)` / B팀: `royal crimson (#641E27)`

---

## 3. GPT — 몬스터 (왕이 소환하는 "공허의 군단" 제안)

공통: 왕의 금빛 소환 마법으로 깨어난 빈 갑옷 군단. 사람과 확실히 다른 **비대칭·위협적 실루엣**, 금이 간 틈에서 약한 금빛이 새어 나옴 (발광은 절제).
3D 생성용은 몬스터도 정면 한 장 + 시트. 자세는 A포즈 대신 **걷는 중간 자세로 고정** (뼈 없이 쓰기 때문).

```
Summoned monster for a dark fantasy game: [MONSTER]. Full body, three-quarter front view, mid-stride walking pose
frozen in place, entire body visible, plain flat mid-grey background, even lighting. Asymmetric, menacing
silhouette clearly different from human knights. Hollow animated armor with faint golden rune light leaking
from cracks (subtle, small areas only). [공통 스타일 문구]
```

| 파일 | [MONSTER] |
|---|---|
| `monster_0_grunt` | `a small hunched hollow footsoldier, 1.2 m tall, mismatched rusted armor plates, one oversized jagged blade arm, fast and light` |
| `monster_1_tank` | `a huge hulking hollow brute, 1.8 m tall and very wide, massive slab-like armor, one giant shield fused to its left arm, slow and heavy` |
| `monster_2_ranged` | `a thin tall hollow caster, 1.1 m tall (hovering slightly hunched), tattered hood and robes over a cage-like armor torso, a glowing golden orb held in a skeletal gauntlet` |

팀 구분은 금빛 틈 색은 그대로 두고 **천 조각 색**만 A팀 청색 / B팀 적색.

---

## 4. GPT — 칼

```
A single combat knife for a dark fantasy royal knight, 35 cm total length: straight double-edged steel blade
(20 cm) with a subtle fuller, short gilded crossguard with a small crown motif, dark leather-wrapped grip,
round gold pommel. Product shot, side view, blade pointing right, plain mid-grey background, even lighting,
realistic steel and aged gold materials. [공통 스타일 문구]
```
1인칭 손 포함용 (선택):
```
First-person view of a gauntleted right hand gripping the same knife, blade forward, as seen from the
player's eyes in a first-person game, lower right of frame, plain mid-grey background. [공통 스타일 문구]
```

---

## 5. VARCO 3D 생성

넣는 이미지: **2-1 정면 한 장** (몬스터·칼은 해당 이미지). 시트는 넣지 않는다 (여러 명으로 인식될 수 있음).
설정에서 고를 수 있으면: **텍스처 포함 / PBR / 중간 폴리곤 / FBX**.
텍스트 프롬프트 칸이 있으면:
```
Game-ready realistic character, single mesh, clean topology, symmetrical body, A-pose,
PBR textures (base color, normal, roughness, metallic), real-world scale 1.8 m tall,
no base or pedestal, no background objects.
```
몬스터는 `A-pose` → `walking pose, static mesh`, 칼은 `single prop, 35 cm` 으로 바꾼다.

받은 뒤 확인할 것: 키가 사람 크기인지, 망토가 다리와 붙어 있지 않은지, 손가락이 뭉개지지 않았는지. 문제 있으면 다시 뽑거나 그대로 보내 주면 게임에서 맞춘다.

---

## 6. 영상 AI — 동작 (리깅된 모델 렌더 이미지를 넣고)

정면용·옆면용 두 번씩. 모든 동작 공통 앞부분:
```
The exact same character as the input image, full body always in frame, locked-off static camera,
[front view / right side view], plain flat grey background, even lighting, no camera movement, no cuts,
no other characters, realistic weight and momentum, armor and cape follow the body naturally.
```

| 클립 | 길이 | 뒤에 붙일 문장 |
|---|---|---|
| `idle` | 4초 루프 | `standing at ease, breathing slowly, slight weight shift, returns exactly to the starting pose (seamless loop)` |
| `walk` | 1초 루프 | `walking in place at a steady pace (2.5 m/s feel), arms swinging naturally, seamless loop` |
| `run` | 0.7초 루프 | `running in place, fast athletic stride (5 m/s feel), seamless loop` |
| `crouch_idle` | 2초 루프 | `crouched low, knees bent, alert, small breathing motion, seamless loop` |
| `crouch_walk` | 1.2초 루프 | `moving forward in a low crouch, careful steps, seamless loop` |
| `jump` | 1초 | `a quick vertical jump from standing and landing back in the same spot, knees absorbing the landing` |
| `slash` | **0.45초** | `a very fast horizontal knife slash with the right hand from left to right, the blade crosses the front of the body almost instantly (within the first 0.06 s), then recovers to a ready stance` |
| `stab` | **1.0초** | `a short wind-up then a powerful forward knife thrust with the right hand at chest height (blade fully extended at 0.22 s), then pulls back to a ready stance` |
| `hit_react` | 0.3초 | `upper body flinches backward from a hit to the chest, then recovers` |
| `death` | 1.5초 | `collapses forward to the ground realistically and stays still at the end` |
| `summon` (왕) | 0.8초 | `raises both hands forward and upward commandingly as if releasing golden magic, then lowers them` |
| `buff` (왕) | 0.8초 | `raises the right hand high above the head as if blessing allies, then lowers it` |

칼은 영상에서 잘 안 나오면 **빈손으로 쥔 자세**로 뽑고, 칼은 게임에서 오른손 뼈에 붙인다.

---

## 7. VARCO Sound

공통 끝 문장: `game sound effect, clean, dry, no music, no voice unless stated, short tail, high quality`
(음악은 `game soundtrack` 으로 바꾸고 끝에 `seamless loop`)

### 전투
| 파일 | 프롬프트 |
|---|---|
| `swing_01~03` | `fast whoosh of a short steel knife slicing through air, sharp and quick, 0.4 seconds` |
| `stab_01~03` | `short heavy thrust whoosh of a knife stabbing forward, slight cloth rustle, 0.4 seconds` |
| `hit_body_01~03` | `knife striking a plate-armored body, metal clank mixed with a dull flesh impact, 0.3 seconds` |
| `hit_head_01~03` | `sharp brutal knife hit on a steel helmet, bright metallic ring plus heavy thud, 0.3 seconds` |
| `hit_monster_01~03` | `blade cutting into hollow cursed armor, hollow metallic crack with a faint magical crackle, 0.3 seconds` |
| `backstab` | `deadly knife stab from behind, wet piercing impact with a deep ominous low boom, 0.6 seconds` |
| `kill_confirm` | `short satisfying UI confirmation sound for a kill, crisp metallic chime, 0.3 seconds` |
| `hurt_01~03` | `male knight grunting in pain after being hit, short, armor rattle, 0.4 seconds` (voice allowed) |
| `death_01~02` | `armored male knight collapsing to the ground, last breath, heavy armor clatter on wood, 1 second` |
| `footstep_wood_01~04` | `single armored boot step on old wooden bridge planks, 0.2 seconds` |
| `footstep_stone_01~04` | `single armored boot step on stone floor, 0.2 seconds` |
| `jump` / `land` | `armored knight jumping, short cloth and metal rustle` / `armored knight landing on wooden planks, heavy thump` |

### 왕 · 몬스터
| 파일 | 프롬프트 |
|---|---|
| `summon_0` | `golden magic summoning a small creature, quick rising shimmer and a light metallic materialization, 0.8 seconds` |
| `summon_1` | `golden magic summoning a huge armored brute, deep rumbling build-up then heavy metal impact, 0.8 seconds` |
| `summon_2` | `golden magic summoning a ghostly caster, airy whispering shimmer and a soft hum, 0.8 seconds` |
| `buff_cast` | `holy golden blessing spell, warm rising choir-like shimmer and soft bells, 1 second` |
| `monster_attack_0/1/2` | `small hollow armor creature slashing, quick scrape` / `huge armored brute smashing with a shield, heavy crash` / `ghostly caster firing a golden orb, magical zap` |
| `monster_death_0/1/2` | `hollow armor falling apart into scrap metal, golden light fading` (크기에 맞게 small / huge / ghostly) |
| `king_down` | `the king falls in battle, deep dramatic orchestral hit with a bell toll and armor crash, 2 seconds` |

### 라운드 · UI
| 파일 | 프롬프트 |
|---|---|
| `round_start` | `medieval war horn blast with a big war drum hit, round start, 1.5 seconds` |
| `countdown_tick` | `short low wooden drum tick for a countdown, 0.2 seconds` |
| `round_win` / `round_lose` | `short triumphant brass fanfare, 2 seconds` / `short somber low brass phrase, 2 seconds` |
| `match_win` / `match_lose` | `epic victorious orchestral finale with choir, 4 seconds` / `tragic defeat orchestral ending, slow and dark, 4 seconds` |
| `match_found` | `match found alert, bright rising metallic chime with a drum hit, 1.5 seconds` |
| `ui_click` / `ui_hover` | `soft metallic UI click, 0.1 seconds` / `very subtle soft UI hover tick, 0.1 seconds` |
| `select_class` | `sword being drawn slightly from a sheath, UI confirm, 0.3 seconds` |
| `rank_up` / `rank_down` | `ascending heroic chime with shimmer, promotion, 2 seconds` / `descending muted chime, demotion, 2 seconds` |

### 음악
| 파일 | 프롬프트 |
|---|---|
| `music_menu` | `dark fantasy orchestral main menu theme, slow and majestic, low strings, distant choir, royal and tragic mood, 2 minutes, seamless loop` |
| `music_select` | `tense dark fantasy preparation music, low pulsing strings and taiko drums, 30 seconds, seamless loop` |
| `music_battle` | `dark fantasy battle underscore, driving low percussion and strings, mid-low frequencies only so footsteps stay clear, not too busy, 2 minutes, seamless loop` |
| `ambience_bridge` | `windy high bridge ambience, gusts, distant flags flapping, faint creaking wood, no music, 1 minute, seamless loop` |

---

## 8. 넘겨줄 때
- 파일 이름은 `asset-spec.md` 표 이름 그대로.
- 캐릭터는 FBX + 텍스처 폴더 통째로, 동작은 클립 이름별로.
- 하나(기사 1명)만 먼저 넘겨주면 게임에 넣어서 크기·칼 위치·타이밍을 확인한 뒤 나머지를 진행.
