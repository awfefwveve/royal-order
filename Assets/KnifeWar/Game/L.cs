using System;
using System.Collections.Generic;
using UnityEngine;

namespace KnifeWar.Game
{
    public enum Lang { Ko = 0, En = 1, Ja = 2, ZhCN = 3 }

    /// <summary>
    /// 다국어 문자열. 화면에 보이는 모든 글자는 여기 키로만 쓴다 (코드에 직접 쓰지 않기).
    /// 출시 국가가 늘면 열을 추가하고, 번역가 작업용으로는 이 표를 CSV로 내보내면 된다.
    /// 사용: L.T("hp", 100) → "체력 100" / "HP 100" ...
    /// </summary>
    public static class L
    {
        public static Lang Current { get; private set; } = Detect();
        public static event Action Changed;
        public static readonly string[] LangNames = { "한국어", "English", "日本語", "简体中文" };

        static Lang Detect()
        {
            int saved = PlayerPrefs.GetInt("kw.lang", -1);
            if (saved >= 0 && saved <= 3) return (Lang)saved;
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Korean: return Lang.Ko;
                case SystemLanguage.Japanese: return Lang.Ja;
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified: return Lang.ZhCN;
                default: return Lang.En;
            }
        }

        public static void Set(Lang lang)
        {
            if (Current == lang) return;
            Current = lang;
            PlayerPrefs.SetInt("kw.lang", (int)lang);
            Changed?.Invoke();
        }

        public static string T(string key)
        {
            if (Table.TryGetValue(key, out var row))
            {
                string s = row[(int)Current];
                return string.IsNullOrEmpty(s) ? row[1] : s;   // 번역 없으면 영어
            }
            return key;
        }

        public static string T(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch (FormatException) { return T(key); }
        }

        //                                  한국어, English, 日本語, 简体中文
        static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>
        {
            // ---- 게임 이름·공통 ----
            ["title"] = new[] { "ROYAL ORDER", "ROYAL ORDER", "ROYAL ORDER", "ROYAL ORDER" },   // 임시 게임 이름 (브랜드: ASTREL)
            ["knight"] = new[] { "기사", "Knight", "騎士", "骑士" },
            ["wizard"] = new[] { "왕", "King", "王", "国王" },
            ["me"] = new[] { "나", "Me", "自分", "我" },
            ["team_blue"] = new[] { "블루", "Blue", "ブルー", "蓝" },
            ["team_red"] = new[] { "레드", "Red", "レッド", "红" },
            ["player_n"] = new[] { "플레이어{0}", "Player{0}", "プレイヤー{0}", "玩家{0}" },
            ["monster"] = new[] { "몬스터", "Monster", "モンスター", "怪物" },

            // ---- 상단 HUD ----
            ["round_info"] = new[] { "라운드 {0}  ·  {1}선승", "Round {0}  ·  First to {1}", "ラウンド {0}  ·  {1}本先取", "第 {0} 回合  ·  先胜 {1}" },
            ["alive"] = new[] { "생존 {0}", "Alive {0}", "生存 {0}", "存活 {0}" },
            ["monsters_n"] = new[] { "몬스터 {0}", "Monsters {0}", "モンスター {0}", "怪物 {0}" },
            ["draw"] = new[] { "무승부", "Draw", "引き分け", "平局" },
            ["round_win"] = new[] { "라운드 승리", "Round Won", "ラウンド勝利", "回合胜利" },
            ["round_lose"] = new[] { "라운드 패배", "Round Lost", "ラウンド敗北", "回合失败" },
            ["victory"] = new[] { "승리!", "Victory!", "勝利！", "胜利！" },
            ["defeat"] = new[] { "패배", "Defeat", "敗北", "失败" },
            ["press_r"] = new[] { "R 키를 누르면 다시 시작", "Press R to restart", "Rキーで再スタート", "按 R 重新开始" },
            ["click_to_start"] = new[] { "화면을 클릭하면 조작 시작 (Esc = 메뉴, Tab = 점수판)", "Click to play (Esc = Menu, Tab = Scoreboard)", "クリックで操作開始 (Esc = メニュー, Tab = スコア)", "点击屏幕开始操作 (Esc = 菜单, Tab = 记分板)" },

            // ---- 직업 선택 ----
            ["class_select"] = new[] { "직업 선택  ({0}초)", "Choose Your Class  ({0}s)", "クラス選択  ({0}秒)", "选择职业  ({0}秒)" },
            ["knight_title"] = new[] { "[1] 황실기사단", "[1] Royal Knight", "[1] 王立騎士団", "[1] 皇家骑士团" },
            ["knight_desc"] = new[] { "칼 — 좌클 베기 / 우클 찌르기", "Blade — LMB slash / RMB stab", "剣 — 左クリック斬り / 右クリック突き", "刀 — 左键斩击 / 右键突刺" },
            ["wizard_title"] = new[] { "[2] 왕 (팀당 1명)", "[2] King (1 per team)", "[2] 王 (チームに1人)", "[2] 国王 (每队1名)" },
            ["wizard_desc"] = new[] { "1·2·3 소환 / Q 버프 (공격 없음)", "1·2·3 summon / Q buff (no attack)", "1·2·3 召喚 / Q バフ (攻撃なし)", "1·2·3 召唤 / Q 增益 (无攻击)" },
            ["team_counts"] = new[] { "우리 팀: 기사 {0} / 왕 {1}", "Your team: {0} Knights / {1} King", "味方: 騎士 {0} / 王 {1}", "我方: 骑士 {0} / 国王 {1}" },

            // ---- 하단 HUD ----
            ["hp"] = new[] { "체력 {0}", "HP {0}", "HP {0}", "生命 {0}" },
            ["dead_spectate"] = new[] { "사망 — 다음 라운드까지 관전", "Dead — spectating until next round", "死亡 — 次のラウンドまで観戦", "阵亡 — 观战至下一回合" },
            ["mana"] = new[] { "마나 {0}", "Mana {0}", "マナ {0}", "法力 {0}" },
            ["wizard_keys"] = new[] { "1 졸병({0})  2 탱커({1})  3 원거리({2})  Q 버프({3})", "1 Grunt({0})  2 Tank({1})  3 Ranged({2})  Q Buff({3})", "1 兵士({0})  2 タンク({1})  3 遠距離({2})  Q バフ({3})", "1 步兵({0})  2 坦克({1})  3 远程({2})  Q 增益({3})" },
            ["knight_keys"] = new[] { "좌클 베기 · 우클 찌르기 · 등 뒤 찌르기 즉사", "LMB slash · RMB stab · Backstab kills", "左クリック斬り · 右クリック突き · 背後突きで即死", "左键斩击 · 右键突刺 · 背刺秒杀" },
            ["buff_active"] = new[] { "버프: 기사 이동속도 +15%", "Buff: Knight speed +15%", "バフ: 騎士の移動速度 +15%", "增益: 骑士移速 +15%" },

            // ---- 점수판 ----
            ["sb_name"] = new[] { "이름", "Name", "名前", "名称" },
            ["sb_class"] = new[] { "직업", "Class", "クラス", "职业" },
            ["sb_kills"] = new[] { "킬", "Kills", "キル", "击杀" },
            ["sb_deaths"] = new[] { "데스", "Deaths", "デス", "死亡" },
            ["sb_damage"] = new[] { "피해량", "Damage", "ダメージ", "伤害" },
            ["sb_dead"] = new[] { "(사망)", "(dead)", "(死亡)", "(阵亡)" },

            // ---- Esc 메뉴 ----
            ["menu"] = new[] { "메뉴", "Menu", "メニュー", "菜单" },
            ["sensitivity"] = new[] { "마우스 감도  {0:0.00}", "Mouse sensitivity  {0:0.00}", "マウス感度  {0:0.00}", "鼠标灵敏度  {0:0.00}" },
            ["language"] = new[] { "언어", "Language", "言語", "语言" },
            ["fe_ready"] = new[] { "준비 완료", "Ready", "準備完了", "准备完毕" },
            ["fe_loading"] = new[] { "불러오는 중", "Loading", "読み込み中", "加载中" },
            ["fe_offline_play"] = new[] { "계정 없이 연습전 하기", "Practice offline (no account)", "アカウントなしで練習", "无账号练习" },
            ["fe_dev"] = new[] { "개발자 도구", "Developer tools", "開発者ツール", "开发者工具" },
            ["fe_verify"] = new[] { "이메일 인증", "Verify email", "メール認証", "验证邮箱" },
            ["fe_play"] = new[] { "플레이", "PLAY", "プレイ", "开始游戏" },
            ["fe_practice"] = new[] { "연습전", "Practice", "練習", "练习" },
            ["fe_quit"] = new[] { "게임 종료", "Quit", "ゲーム終了", "退出游戏" },
            ["fe_in_party"] = new[] { "파티 {0}명", "Party of {0}", "パーティー {0}人", "队伍 {0}人" },
            ["fe_custom"] = new[] { "사용자 설정", "Custom", "カスタム", "自定义" },
            ["room_desc"] = new[] { "친구와 방을 만들어 원하는 팀으로 대전해요. 빈 자리는 봇이 채워요. 점수(MMR)에는 반영되지 않아요.", "Make a room with friends and pick your teams. Empty seats are filled by bots. Doesn't affect MMR.", "フレンドとルームを作り、好きなチームで対戦。空き枠はボットが入ります。MMRには影響しません。", "和好友开房间自选队伍对战，空位由机器人补上。不影响MMR。" },
            ["room_create"] = new[] { "방 만들기", "Create room", "ルーム作成", "创建房间" },
            ["room_code"] = new[] { "방 코드  {0}", "Room code  {0}", "ルームコード  {0}", "房间代码  {0}" },
            ["room_invite"] = new[] { "친구 초대", "Invite", "招待", "邀请好友" },
            ["room_team_a"] = new[] { "A팀", "Team A", "Aチーム", "A队" },
            ["room_team_b"] = new[] { "B팀", "Team B", "Bチーム", "B队" },
            ["room_bot"] = new[] { "봇", "Bot", "ボット", "机器人" },
            ["room_move"] = new[] { "여기로 이동", "Move here", "ここへ移動", "移到这里" },
            ["room_start"] = new[] { "시작", "Start", "開始", "开始" },
            ["room_wait_host"] = new[] { "방장이 시작하길 기다리는 중", "Waiting for host", "ホストの開始待ち", "等待房主开始" },
            ["room_starting"] = new[] { "경기 준비 중…", "Starting…", "準備中…", "准备中…" },
            ["room_leave"] = new[] { "방 나가기", "Leave room", "退出", "离开房间" },
            ["room_started"] = new[] { "이미 경기가 시작된 방이에요", "That room's match has already started", "既に開始されたルームです", "该房间已开始比赛" },
            ["room_no_server"] = new[] { "지금 비어 있는 게임 서버가 없어요. 잠시 뒤 다시 해 주세요", "No game server is free right now. Try again shortly", "空いているサーバーがありません。少し後で", "暂无空闲服务器，请稍后再试" },
            ["room_busy"] = new[] { "방에 다른 경기 중인 사람이 있어요", "Someone in the room is in another match", "他の試合中の人がいます", "房间里有人正在其他比赛中" },
            ["fr_title"] = new[] { "친구", "Friends", "フレンド", "好友" },
            ["fr_hint"] = new[] { "이름#태그", "Name#Tag", "名前#タグ", "名字#标签" },
            ["fr_add"] = new[] { "추가", "Add", "追加", "添加" },
            ["fr_sent"] = new[] { "친구 요청을 보냈어요", "Friend request sent", "申請を送りました", "已发送好友请求" },
            ["fr_now_friends"] = new[] { "친구가 됐어요", "You're now friends", "フレンドになりました", "已成为好友" },
            ["fr_not_found"] = new[] { "그 이름#태그를 찾을 수 없어요", "Couldn't find that Name#Tag", "見つかりません", "找不到该名字#标签" },
            ["fr_failed"] = new[] { "실패했어요. 다시 해 주세요", "Failed. Please try again", "失敗しました", "失败，请重试" },
            ["fr_requests"] = new[] { "받은 요청 {0}", "Requests {0}", "申請 {0}", "好友请求 {0}" },
            ["fr_accept"] = new[] { "수락", "Accept", "承認", "接受" },
            ["fr_decline"] = new[] { "거절", "Decline", "拒否", "拒绝" },
            ["fr_list"] = new[] { "친구 {0}", "Friends {0}", "フレンド {0}", "好友 {0}" },
            ["fr_empty"] = new[] { "위에 친구의 이름#태그를 넣어 추가해요", "Add friends by their Name#Tag above", "上で名前#タグを入れて追加", "在上方输入名字#标签添加好友" },
            ["fr_invite"] = new[] { "초대", "Invite", "招待", "邀请" },
            ["fr_invited"] = new[] { "{0}님을 초대했어요", "Invited {0}", "{0}さんを招待しました", "已邀请 {0}" },
            ["fr_pending"] = new[] { "보낸 요청", "Sent", "送信済み", "已发送" },
            ["fr_state_online"] = new[] { "온라인", "Online", "オンライン", "在线" },
            ["fr_state_searching"] = new[] { "매칭 중", "In queue", "マッチング中", "匹配中" },
            ["fr_state_in_match"] = new[] { "경기 중", "In match", "試合中", "比赛中" },
            ["fr_state_offline"] = new[] { "오프라인", "Offline", "オフライン", "离线" },
            ["fr_invite_msg"] = new[] { "{0}님이 {1}에 초대했어요", "{0} invited you to {1}", "{0}さんが{1}に招待しました", "{0} 邀请你加入{1}" },
            ["fr_join"] = new[] { "참가", "Join", "参加", "加入" },
            ["fe_ranked"] = new[] { "경쟁전", "Ranked", "ランク", "排位赛" },
            ["fe_practice_bots"] = new[] { "연습전 (봇)", "Practice (bots)", "練習 (ボット)", "练习 (机器人)" },
            ["fe_party_code"] = new[] { "파티 코드  {0}", "Party code  {0}", "パーティーコード  {0}", "队伍代码  {0}" },
            ["fe_copy"] = new[] { "복사", "Copy", "コピー", "复制" },
            ["fe_copied"] = new[] { "복사했어요", "Copied", "コピーしました", "已复制" },
            ["fe_create_party"] = new[] { "파티 만들기", "Create party", "パーティー作成", "创建队伍" },
            ["fe_join_code"] = new[] { "코드로 참가", "Join by code", "コードで参加", "用代码加入" },
            ["fe_mode_desc"] = new[] { "경쟁전: 6대6 · 7선승. 결과에 따라 MMR과 티어가 바뀌어요. 빈 칸을 누르면 파티 코드가 복사돼요.", "Ranked: 6v6, first to 7. Your MMR and tier change with the result. Click an empty slot to copy the party code.", "ランク: 6対6・7本先取。結果でMMRとティアが変わります。空き枠でパーティーコードをコピー。", "排位赛: 6对6・先赢7局。结果会改变MMR和段位。点击空位复制队伍代码。" },
            ["fe_waiting"] = new[] { "대기 중  {0}:{1:00}", "Searching  {0}:{1:00}", "マッチング中  {0}:{1:00}", "匹配中  {0}:{1:00}" },
            ["fe_wait_leader"] = new[] { "파티장을 기다리는 중", "Waiting for party leader", "リーダーを待っています", "等待队长" },
            ["fe_cancel_hint"] = new[] { "누르면 취소", "Click to cancel", "クリックでキャンセル", "点击取消" },
            ["fe_find"] = new[] { "매칭 시작", "FIND MATCH", "マッチ開始", "开始匹配" },
            ["fe_leave_party"] = new[] { "파티 나가기", "Leave party", "パーティー離脱", "退出队伍" },
            ["fe_invite"] = new[] { "초대", "Invite", "招待", "邀请" },
            ["fe_leader"] = new[] { "파티장", "Leader", "リーダー", "队长" },
            ["fe_ready_tag"] = new[] { "준비", "Ready", "準備", "准备" },
            ["fe_me"] = new[] { "나", "You", "自分", "我" },
            ["fe_map"] = new[] { "외나무다리", "The Narrow Bridge", "一本橋", "独木桥" },
            ["fe_tip1"] = new[] { "공격자가 본 순간으로 되감아 판정해요. 내 화면에서 맞았으면 맞은 거예요.", "Hits are judged at the moment the attacker saw. If it hit on your screen, it hit.", "攻撃者が見た瞬間に巻き戻して判定します。", "按攻击者看到的那一刻回溯判定。" },
            ["fe_tip2"] = new[] { "왕은 팀당 한 명. 왕을 지키고 상대 왕을 노리세요.", "One king per team. Guard yours, hunt theirs.", "王はチームに一人。自分の王を守り、相手の王を狙おう。", "每队只有一名国王。保护己方国王，猎杀对方国王。" },
            ["fe_tip3"] = new[] { "뒤에서 찌르면 백스탭 — 큰 피해를 줘요.", "Stab from behind for a backstab — big damage.", "背後から突くとバックスタブ — 大ダメージ。", "从背后突刺造成背刺 — 高额伤害。" },
            ["fe_result_loading"] = new[] { "결과 불러오는 중", "Loading results", "結果を読み込み中", "正在加载结果" },
            ["fe_my_team"] = new[] { "우리 팀", "Your team", "味方", "我方" },
            ["fe_enemy_team"] = new[] { "상대 팀", "Enemy team", "敵", "敌方" },
            ["fe_no_record"] = new[] { "이번 경기는 전적에 남지 않아요", "This match isn't recorded", "この試合は記録されません", "本场不计入战绩" },
            ["fe_placement"] = new[] { "배치 경기 {0}/5", "Placement {0}/5", "配置試合 {0}/5", "定级赛 {0}/5" },
            ["fe_promoted"] = new[] { "승급!  {0}", "PROMOTED  {0}", "昇格!  {0}", "晋级!  {0}" },
            ["fe_demoted"] = new[] { "강등  {0}", "DEMOTED  {0}", "降格  {0}", "降级  {0}" },
            ["fe_continue"] = new[] { "계속", "Continue", "続ける", "继续" },
            ["settings"] = new[] { "설정", "Settings", "設定", "设置" },
            ["close"] = new[] { "닫기", "Close", "閉じる", "关闭" },
            ["set_controls"] = new[] { "조작", "Controls", "操作", "操作" },
            ["set_video"] = new[] { "화면", "Video", "画面", "画面" },
            ["set_audio"] = new[] { "소리", "Audio", "サウンド", "声音" },
            ["set_invert_y"] = new[] { "마우스 상하 반전", "Invert mouse Y", "マウス上下反転", "鼠标Y轴反转" },
            ["set_press_key"] = new[] { "키를 누르세요", "Press a key", "キーを押す", "请按键" },
            ["set_reset_keys"] = new[] { "키 기본값", "Default keys", "キー初期化", "默认按键" },
            ["set_apply_screen"] = new[] { "화면 적용", "Apply display", "画面を適用", "应用画面" },
            ["set_fov"] = new[] { "시야각  {0:0}", "Field of view  {0:0}", "視野角  {0:0}", "视野  {0:0}" },
            ["set_display"] = new[] { "화면 모드", "Display mode", "画面モード", "显示模式" },
            ["set_borderless"] = new[] { "전체 창", "Borderless", "ボーダーレス", "无边框" },
            ["set_windowed"] = new[] { "창 모드", "Windowed", "ウィンドウ", "窗口" },
            ["set_fullscreen"] = new[] { "전체화면", "Fullscreen", "フルスクリーン", "全屏" },
            ["set_resolution"] = new[] { "해상도", "Resolution", "解像度", "分辨率" },
            ["set_native"] = new[] { "모니터 기본", "Native", "ネイティブ", "原生" },
            ["set_vsync"] = new[] { "수직 동기화", "V-Sync", "垂直同期", "垂直同步" },
            ["set_fps_cap"] = new[] { "FPS 제한", "FPS limit", "FPS制限", "帧率上限" },
            ["set_unlimited"] = new[] { "없음", "None", "なし", "无" },
            ["set_show_fps"] = new[] { "FPS 표시", "Show FPS", "FPS表示", "显示帧率" },
            ["set_vol_master"] = new[] { "전체 음량  {0}", "Master volume  {0}", "全体音量  {0}", "总音量  {0}" },
            ["set_vol_sfx"] = new[] { "효과음  {0}", "Effects  {0}", "効果音  {0}", "音效  {0}" },
            ["set_vol_music"] = new[] { "음악  {0}", "Music  {0}", "音楽  {0}", "音乐  {0}" },
            ["key_lmb"] = new[] { "마우스 왼쪽", "Left mouse", "左クリック", "鼠标左键" },
            ["key_rmb"] = new[] { "마우스 오른쪽", "Right mouse", "右クリック", "鼠标右键" },
            ["key_mmb"] = new[] { "마우스 휠", "Middle mouse", "ホイール", "鼠标中键" },
            ["bind_forward"] = new[] { "앞으로", "Forward", "前進", "前进" },
            ["bind_back"] = new[] { "뒤로", "Back", "後退", "后退" },
            ["bind_left"] = new[] { "왼쪽", "Left", "左", "左" },
            ["bind_right"] = new[] { "오른쪽", "Right", "右", "右" },
            ["bind_jump"] = new[] { "점프", "Jump", "ジャンプ", "跳跃" },
            ["bind_crouch"] = new[] { "앉기", "Crouch", "しゃがむ", "蹲下" },
            ["bind_walk"] = new[] { "걷기", "Walk", "歩く", "慢走" },
            ["bind_slash"] = new[] { "베기", "Slash", "斬り", "挥砍" },
            ["bind_stab"] = new[] { "찌르기", "Stab", "突き", "突刺" },
            ["bind_slot1"] = new[] { "1번 (직업·소환)", "Slot 1 (class/summon)", "1番 (職業·召喚)", "1号 (职业·召唤)" },
            ["bind_slot2"] = new[] { "2번 (직업·소환)", "Slot 2 (class/summon)", "2番 (職業·召喚)", "2号 (职业·召唤)" },
            ["bind_slot3"] = new[] { "3번 (소환)", "Slot 3 (summon)", "3番 (召喚)", "3号 (召唤)" },
            ["bind_buff"] = new[] { "버프 (왕)", "Buff (King)", "バフ (王)", "增益 (国王)" },
            ["bind_scoreboard"] = new[] { "점수판", "Scoreboard", "スコアボード", "记分板" },
            ["resume"] = new[] { "계속하기", "Resume", "再開", "继续" },
            ["restart"] = new[] { "처음부터 다시", "Restart", "最初から", "重新开始" },
            ["leave"] = new[] { "나가기", "Leave", "退出", "离开" },
            ["esc_close"] = new[] { "Esc로 닫기", "Esc to close", "Escで閉じる", "按 Esc 关闭" },

            // ---- 처치 방식 ----
            ["how_slash"] = new[] { "베기", "Slash", "斬り", "斩击" },
            ["how_stab"] = new[] { "찌르기", "Stab", "突き", "突刺" },
            ["how_head"] = new[] { "헤드", "Head", "ヘッド", "爆头" },
            ["how_backstab"] = new[] { "백스탭", "Backstab", "バックスタブ", "背刺" },
            ["how_fall"] = new[] { "추락", "Fall", "落下", "坠落" },
            ["how_monster"] = new[] { "몬스터", "Monster", "モンスター", "怪物" },

            // ---- 알림 ----
            ["msg_no_mana"] = new[] { "마나 부족", "Not enough mana", "マナ不足", "法力不足" },
            ["msg_team_cap"] = new[] { "팀 몬스터 상한 도달", "Team monster limit reached", "チームのモンスター上限", "已达队伍怪物上限" },
            ["msg_not_king"] = new[] { "왕만 소환할 수 있어요 (다음 준비 단계에서 2번)", "Only the King can summon (press 2 next round)", "召喚は王のみ (次のラウンドで2)", "只有国王能召唤 (下回合按 2)" },
            ["msg_king_taken"] = new[] { "이미 우리 팀에 왕이 있어요", "Your team already has a King", "既に味方に王がいます", "我方已有国王" },
            ["msg_buff_ok"] = new[] { "버프 발동: 아군 기사 이동속도 +15% (8초)", "Buff: allied Knights +15% speed (8s)", "バフ発動: 味方騎士の移動速度 +15% (8秒)", "增益: 友方骑士移速 +15% (8秒)" },
            ["msg_buff_fail"] = new[] { "버프 실패 (마나 부족)", "Buff failed (not enough mana)", "バフ失敗 (マナ不足)", "增益失败 (法力不足)" },
            ["msg_summon_fail"] = new[] { "소환 실패", "Summon failed", "召喚失敗", "召唤失败" },

            // ---- 시작 메뉴 ----
            ["menu_name"] = new[] { "이름", "Name", "名前", "名称" },
            ["menu_backend"] = new[] { "백엔드", "Backend", "バックエンド", "后端" },
            ["menu_match"] = new[] { "온라인 매칭 시작", "Find Online Match", "オンライン対戦を探す", "开始在线匹配" },
            ["menu_practice"] = new[] { "연습 (봇)", "Practice (Bots)", "練習 (ボット)", "练习 (机器人)" },
            ["menu_host"] = new[] { "방 만들기", "Host Game", "ルーム作成", "创建房间" },
            ["menu_direct"] = new[] { "직접 접속 (주소 · 포트)", "Direct connect (address · port)", "直接接続 (アドレス · ポート)", "直接连接 (地址 · 端口)" },
            ["menu_lag"] = new[] { "테스트용 지연 (핑 추가): {0}ms", "Test latency (added ping): {0}ms", "テスト用遅延 (追加ピング): {0}ms", "测试延迟 (附加延迟): {0}ms" },
            ["menu_connect"] = new[] { "직접 접속", "Connect", "接続", "连接" },
            ["cancel"] = new[] { "취소", "Cancel", "キャンセル", "取消" },
            ["ok"] = new[] { "확인", "OK", "OK", "确定" },

            // ---- 접속·매칭 상태 ----
            ["st_logging_in"] = new[] { "로그인 중...", "Signing in...", "ログイン中...", "登录中..." },
            ["st_backend_down"] = new[] { "백엔드에 연결할 수 없어요", "Can't reach the backend", "バックエンドに接続できません", "无法连接后端" },
            ["st_login_failed"] = new[] { "로그인 실패 ({0})", "Sign-in failed ({0})", "ログイン失敗 ({0})", "登录失败 ({0})" },
            ["st_pinging"] = new[] { "리전 핑 측정 중...", "Measuring region ping...", "リージョンのピング測定中...", "正在测量各区延迟..." },
            ["st_queue_failed"] = new[] { "대기열 참가 실패", "Failed to join queue", "キュー参加に失敗", "加入队列失败" },
            ["st_searching"] = new[] { "매칭 중 {0}:{1:00}{2}", "Searching {0}:{1:00}{2}", "マッチング中 {0}:{1:00}{2}", "匹配中 {0}:{1:00}{2}" },
            ["st_waiting_server"] = new[] { " (빈 서버 대기)", " (waiting for a server)", " (サーバー待ち)", " (等待空闲服务器)" },
            ["st_connecting"] = new[] { "{0}:{1} 접속 중...", "Connecting to {0}:{1}...", "{0}:{1} に接続中...", "正在连接 {0}:{1}..." },
            ["st_connected"] = new[] { "접속됨 (칸 {0})", "Connected (slot {0})", "接続完了 (スロット {0})", "已连接 (位置 {0})" },
            ["st_disconnected"] = new[] { "서버와 연결 끊김", "Disconnected from server", "サーバーとの接続が切れました", "与服务器断开连接" },
            ["st_connect_failed"] = new[] { "접속 실패 — 주소·방화벽을 확인하세요", "Connection failed — check address/firewall", "接続失敗 — アドレス・ファイアウォールを確認", "连接失败 — 请检查地址/防火墙" },
            ["st_full"] = new[] { "방이 가득 찼어요", "Server is full", "満員です", "服务器已满" },
            ["st_bad_address"] = new[] { "주소 오류: {0}", "Invalid address: {0}", "アドレスエラー: {0}", "地址错误: {0}" },
            ["st_ping"] = new[] { "핑 {0}ms", "Ping {0}ms", "ピング {0}ms", "延迟 {0}ms" },
            ["st_reconnecting"] = new[] { "연결이 끊겼어요 — 다시 접속하는 중... ({0}초)", "Connection lost — reconnecting... ({0}s)", "接続が切れました — 再接続中... ({0}秒)", "连接中断 — 正在重新连接... ({0}秒)" },
            ["st_reconnect_failed"] = new[] { "서버에 다시 접속하지 못했어요", "Couldn't reconnect to the server", "サーバーに再接続できませんでした", "无法重新连接服务器" },
            ["st_unstable"] = new[] { "연결 불안정", "Unstable connection", "接続が不安定", "连接不稳定" },
            ["st_rej_version"] = new[] { "게임 버전이 달라요 — 업데이트 후 다시 시도하세요", "Version mismatch — please update the game", "バージョンが違います — アップデートしてください", "版本不一致 — 请更新游戏" },
            ["st_rej_over"] = new[] { "이 경기는 이미 끝났어요", "That match has already ended", "この試合は既に終了しました", "该比赛已结束" },
            ["st_match_over"] = new[] { "경기 종료 — {0}", "Match over — {0}", "試合終了 — {0}", "比赛结束 — {0}" },
            ["st_rejoining"] = new[] { "진행 중인 경기로 돌아가는 중...", "Returning to your match in progress...", "進行中の試合に戻っています...", "正在返回进行中的比赛..." },
            ["menu_leave"] = new[] { "경기 나가기", "Leave match", "試合から退出", "离开比赛" },
            ["menu_quit"] = new[] { "게임 종료", "Quit game", "ゲーム終了", "退出游戏" },
            ["result_win"] = new[] { "승리", "Victory", "勝利", "胜利" },
            ["result_lose"] = new[] { "패배", "Defeat", "敗北", "失败" },
            ["result_draw"] = new[] { "무승부", "Draw", "引き分け", "平局" },
            ["party"] = new[] { "파티", "Party", "パーティー", "队伍" },
            ["party_create"] = new[] { "파티 만들기", "Create party", "パーティー作成", "创建队伍" },
            ["party_join"] = new[] { "참가", "Join", "参加", "加入" },
            ["party_code"] = new[] { "파티 코드: {0}", "Party code: {0}", "パーティーコード: {0}", "队伍代码: {0}" },
            ["party_leave"] = new[] { "파티 나가기", "Leave party", "パーティーを抜ける", "离开队伍" },
            ["party_match"] = new[] { "파티로 온라인 매칭", "Find match with party", "パーティーでマッチング", "组队匹配" },
            ["party_wait_leader"] = new[] { "파티장이 매칭을 시작하면 함께 들어가요", "Waiting for the party leader to start", "リーダーの開始を待っています", "等待队长开始匹配" },
            ["party_leader_only"] = new[] { "매칭은 파티장만 시작할 수 있어요", "Only the party leader can start matchmaking", "マッチングはリーダーのみ開始できます", "只有队长可以开始匹配" },
            ["party_not_found"] = new[] { "파티 코드를 찾을 수 없어요", "Party code not found", "パーティーコードが見つかりません", "找不到该队伍代码" },
            ["party_full"] = new[] { "파티가 가득 찼어요 (최대 6명)", "Party is full (max 6)", "パーティーが満員です (最大6人)", "队伍已满 (最多6人)" },
            ["party_busy"] = new[] { "그 파티는 지금 매칭 중이거나 경기 중이에요", "That party is searching or in a match", "そのパーティーはマッチング中または試合中です", "该队伍正在匹配或比赛中" },
            ["party_tag"] = new[] { " · 파티", " · party", " · パーティー", " · 组队" },
            ["acc_need_login"] = new[] { "먼저 로그인하세요", "Please sign in first", "先にログインしてください", "请先登录" },
            ["acc_verify_needed"] = new[] { "이메일 인증 후 매칭할 수 있어요", "Verify your email to play online", "メール認証後にマッチングできます", "验证邮箱后才能匹配" },
            ["acc_wrong"] = new[] { "이메일 또는 비밀번호가 틀렸어요", "Wrong email or password", "メールまたはパスワードが違います", "邮箱或密码错误" },
            ["acc_locked"] = new[] { "로그인 실패가 많아 15분 동안 잠겼어요", "Too many attempts — locked for 15 minutes", "失敗が多いため15分間ロックされました", "尝试次数过多，已锁定15分钟" },
            ["acc_banned_until"] = new[] { "{0}까지 이용 제한 ({1})", "Suspended until {0} ({1})", "{0}まで利用停止 ({1})", "封禁至 {0} ({1})" },
            ["acc_banned"] = new[] { "영구 이용 제한 ({0})", "Permanently suspended ({0})", "永久利用停止 ({0})", "永久封禁 ({0})" },
            ["acc_pw_mismatch"] = new[] { "비밀번호가 서로 달라요", "Passwords don't match", "パスワードが一致しません", "两次密码不一致" },
            ["acc_pw_short"] = new[] { "비밀번호는 8자 이상", "Password must be 8+ characters", "パスワードは8文字以上", "密码至少8位" },
            ["acc_email_taken"] = new[] { "이미 가입된 이메일이에요", "Email already registered", "既に登録されたメールです", "该邮箱已注册" },
            ["acc_bad_email"] = new[] { "이메일 형식이 아니에요", "Invalid email", "メール形式が正しくありません", "邮箱格式不正确" },
            ["acc_reset_sent"] = new[] { "가입된 이메일이면 재설정 코드를 보냈어요", "If the email exists, a reset code was sent", "登録済みなら再設定コードを送りました", "如邮箱已注册，已发送重置码" },
            ["acc_bad_code"] = new[] { "코드가 맞지 않거나 만료됐어요", "Wrong or expired code", "コードが違うか期限切れです", "验证码错误或已过期" },
            ["acc_email"] = new[] { "이메일", "Email", "メール", "邮箱" },
            ["acc_password"] = new[] { "비밀번호", "Password", "パスワード", "密码" },
            ["acc_password2"] = new[] { "비밀번호 확인", "Confirm", "パスワード確認", "确认密码" },
            ["acc_new_password"] = new[] { "새 비밀번호", "New password", "新しいパスワード", "新密码" },
            ["acc_name"] = new[] { "이름", "Name", "名前", "名字" },
            ["acc_code"] = new[] { "코드 6자리", "6-digit code", "6桁コード", "6位验证码" },
            ["acc_login"] = new[] { "로그인", "Sign in", "ログイン", "登录" },
            ["acc_register"] = new[] { "회원가입", "Create account", "新規登録", "注册" },
            ["acc_register_do"] = new[] { "가입하기", "Create account", "登録する", "注册" },
            ["acc_forgot"] = new[] { "비밀번호 찾기", "Forgot password", "パスワードを忘れた", "忘记密码" },
            ["acc_reset_send"] = new[] { "재설정 코드 받기", "Send reset code", "再設定コードを送る", "发送重置码" },
            ["acc_reset_do"] = new[] { "비밀번호 바꾸기", "Change password", "パスワード変更", "修改密码" },
            ["acc_back"] = new[] { "뒤로", "Back", "戻る", "返回" },
            ["acc_agree_terms"] = new[] { "이용약관·개인정보 수집 동의 (필수)", "I agree to the Terms and Privacy Policy", "利用規約·個人情報に同意 (必須)", "同意用户协议和隐私政策 (必选)" },
            ["acc_view_terms"] = new[] { "약관 보기", "Terms", "規約", "协议" },
            ["acc_view_privacy"] = new[] { "개인정보", "Privacy", "個人情報", "隐私" },
            ["acc_age14"] = new[] { "만 14세 이상입니다 (필수)", "I am 14 years or older", "14歳以上です (必須)", "我已满14岁 (必选)" },
            ["acc_need_terms"] = new[] { "약관에 동의해야 해요", "Please accept the Terms", "規約に同意してください", "请同意用户协议" },
            ["acc_need_age"] = new[] { "만 14세 이상만 가입할 수 있어요", "You must be 14 or older", "14歳以上のみ登録できます", "需年满14岁" },
            ["acc_terms_changed"] = new[] { "약관이 바뀌었어요. 다시 확인해 주세요", "Terms were updated. Please check again", "規約が更新されました。もう一度確認してください", "协议已更新，请重新确认" },
            ["acc_terms_updated"] = new[] { "이용약관·개인정보처리방침이 바뀌었어요.\n동의하면 계속 플레이할 수 있어요.", "Our Terms and Privacy Policy changed.\nAccept to keep playing.", "利用規約が更新されました。\n同意するとプレイを続けられます。", "用户协议已更新。\n同意后可继续游戏。" },
            ["acc_agree_do"] = new[] { "동의하고 계속", "Accept and continue", "同意して続ける", "同意并继续" },
            ["acc_logout"] = new[] { "로그아웃", "Sign out", "ログアウト", "退出登录" },
            ["acc_rank"] = new[] { "{0} · {1} MMR · {2}승 {3}패", "{0} · {1} MMR · {2}W {3}L", "{0} · {1} MMR · {2}勝 {3}敗", "{0} · {1} MMR · {2}胜 {3}负" },
            ["acc_verify_info"] = new[] { "{0}로 보낸 인증 코드 6자리를 입력하세요", "Enter the 6-digit code sent to {0}", "{0} に送った6桁コードを入力", "请输入发送到 {0} 的6位验证码" },
            ["acc_resend"] = new[] { "코드 다시 받기", "Resend code", "コード再送", "重新发送" },
            ["acc_resent"] = new[] { "코드를 다시 보냈어요", "Code sent again", "コードを再送しました", "已重新发送" },
            ["acc_later"] = new[] { "나중에", "Later", "後で", "稍后" },
            ["menu_board"] = new[] { "리더보드", "Leaderboard", "ランキング", "排行榜" },
            ["menu_recent"] = new[] { "최근 경기", "Recent matches", "最近の試合", "最近比赛" },
            ["board_empty"] = new[] { "아직 기록이 없어요", "No rankings yet", "まだ記録がありません", "暂无排名" },
            ["recent_empty"] = new[] { "아직 경기 기록이 없어요", "No matches yet", "まだ試合記録がありません", "暂无比赛记录" },
            ["recent_void"] = new[] { "무효", "Void", "無効", "无效" },
            ["tier_placement"] = new[] { "배치 중", "Placement", "配置中", "定级中" },
            ["tier_0"] = new[] { "견습생", "Apprentice", "見習い", "见习生" },
            ["tier_1"] = new[] { "종자", "Squire", "従者", "侍从" },
            ["tier_2"] = new[] { "기사", "Knight", "騎士", "骑士" },
            ["tier_3"] = new[] { "기사단장", "Knight Captain", "騎士団長", "骑士团长" },
            ["tier_4"] = new[] { "성기사", "Paladin", "聖騎士", "圣骑士" },
            ["tier_5"] = new[] { "근위대장", "Royal Guard", "近衛隊長", "近卫队长" },
            ["tier_6"] = new[] { "왕의 검", "King's Blade", "王の剣", "王之剑" },
            ["rep_btn"] = new[] { "신고", "Report", "通報", "举报" },
            ["rep_cheat"] = new[] { "핵 의심", "Cheating", "チート", "作弊" },
            ["rep_abuse"] = new[] { "욕설", "Abuse", "暴言", "辱骂" },
            ["rep_troll"] = new[] { "고의 트롤", "Trolling", "故意の妨害", "故意捣乱" },
            ["rep_name"] = new[] { "부적절한 이름", "Bad name", "不適切な名前", "不当名称" },
            ["rep_afk"] = new[] { "잠수", "AFK", "放置", "挂机" },
            ["rep_done"] = new[] { "신고했어요. 검토 후 조치할게요", "Report sent. We'll review it", "通報しました。確認後に対応します", "已举报，审核后处理" },
            ["rep_failed"] = new[] { "신고하지 못했어요", "Couldn't send the report", "通報できませんでした", "举报失败" },
            ["upd_checking"] = new[] { "업데이트 확인 중...", "Checking for updates...", "アップデート確認中...", "正在检查更新..." },
            ["upd_scanning"] = new[] { "설치된 파일 확인 중...", "Verifying installed files...", "ファイル確認中...", "正在校验文件..." },
            ["upd_downloading"] = new[] { "업데이트 {0} 받는 중... {1}% ({2})", "Downloading update {0}... {1}% ({2})", "アップデート {0} ダウンロード中... {1}% ({2})", "正在下载更新 {0}... {1}% ({2})" },
            ["upd_applying"] = new[] { "업데이트 적용 중 — 게임이 다시 켜져요", "Applying update — the game will restart", "適用中 — ゲームが再起動します", "正在应用更新 — 游戏将重启" },
            ["upd_failed"] = new[] { "업데이트 실패: {0}", "Update failed: {0}", "アップデート失敗: {0}", "更新失败: {0}" },
            ["upd_failed_manifest"] = new[] { "패치 목록을 받지 못했어요", "Couldn't get the patch list", "パッチ一覧を取得できません", "无法获取补丁列表" },
            ["upd_failed_file"] = new[] { "파일을 받지 못했어요 ({0})", "Couldn't download a file ({0})", "ファイルを取得できません ({0})", "无法下载文件 ({0})" },
            ["upd_retry"] = new[] { "다시 시도", "Retry", "再試行", "重试" },
            ["upd_skip"] = new[] { "그냥 계속", "Continue anyway", "このまま続ける", "继续" },
            ["upd_maintenance"] = new[] { "서버 점검 중이에요. 잠시 후 다시 접속해 주세요", "Servers are under maintenance. Please try again later", "サーバーメンテナンス中です", "服务器维护中，请稍后再试" },
            ["st_search_cancelled"] = new[] { "매칭이 취소됐어요", "Matchmaking was cancelled", "マッチングがキャンセルされました", "匹配已取消" },
        };
    }
}
