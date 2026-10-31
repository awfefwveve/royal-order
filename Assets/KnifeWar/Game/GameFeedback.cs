using System;
using System.Collections.Generic;
using KnifeWar.Core;
using UnityEngine;

namespace KnifeWar.Game
{
    /// <summary>
    /// 타격감: 사운드(절차적으로 생성 — 사운드 에셋 없이), 데미지 숫자, 피격 반짝임, 피격 화면.
    /// 정식 사운드·이펙트가 들어오면 여기만 교체하면 된다.
    /// </summary>
    public sealed class GameFeedback : MonoBehaviour
    {
        MatchController _m;
        AudioSource _ui, _music;
        AudioClip _swing, _stab, _hit, _head, _kill, _hurt, _summon, _beep, _go, _win, _lose, _monsterHit, _death;

        struct Popup { public Vector3 Pos; public string Text; public Color Color; public float Born; }
        readonly List<Popup> _popups = new List<Popup>();
        readonly float[] _flashUntil = new float[MatchController.MaxPlayers];
        MaterialPropertyBlock _mpb;
        GUIStyle _popupStyle;
        Texture2D _vignette;

        public float HurtAmount;   // 0~1, 피격 시 화면 붉게

        public void Init(MatchController m)
        {
            _m = m;
            _mpb = new MaterialPropertyBlock();
            BuildClips();
            ApplySkinClips(SkinSet.Current);
            BuildVignette();
        }

        void Start()
        {
            // 사람 카메라에 붙여서 2D 사운드 재생
            var cam = _m.HumanCamera;
            _ui = (cam != null ? cam.gameObject : gameObject).AddComponent<AudioSource>();
            _ui.playOnAwake = false;
            _ui.spatialBlend = 0f;
            ApplyVolume();
            GameSettings.Changed += ApplyVolume;

            var skin = SkinSet.Current;
            if (skin != null && skin.Music != null)
            {
                _music = _ui.gameObject.AddComponent<AudioSource>();
                _music.clip = skin.Music;
                _music.loop = true;
                _music.spatialBlend = 0f;
                ApplyVolume();
                _music.Play();
            }
        }

        void OnDestroy() => GameSettings.Changed -= ApplyVolume;

        /// <summary>효과음은 _ui 볼륨(PlayOneShot에 곱해짐)과 3D 재생 볼륨에, 음악은 따로.</summary>
        void ApplyVolume()
        {
            if (_ui != null) _ui.volume = GameSettings.Sfx;
            if (_music != null) _music.volume = 0.6f * GameSettings.Music;
        }

        /// <summary>SkinSet에 넣은 소리로 교체 (빈 칸은 기본 합성음 유지).</summary>
        void ApplySkinClips(SkinSet k)
        {
            if (k == null) return;
            AudioClip Or(AudioClip custom, AudioClip fallback) => custom != null ? custom : fallback;
            _swing = Or(k.Swing, _swing);
            _stab = Or(k.Stab, _stab);
            _hit = Or(k.HitBody, _hit);
            _head = Or(k.HitHead, _head);
            _kill = Or(k.Kill, _kill);
            _hurt = Or(k.Hurt, _hurt);
            _death = Or(k.Death, _death);
            _summon = Or(k.Summon, _summon);
            _monsterHit = Or(k.MonsterHit, _monsterHit);
            _beep = Or(k.SelectBeep, _beep);
            _go = Or(k.RoundStart, _go);
            _win = Or(k.Win, _win);
            _lose = Or(k.Lose, _lose);
        }

        // ================================================================
        // 이벤트 (MatchController가 부른다)
        // ================================================================

        public void OnSwing(int player, KnifeAttack type)
        {
            Play(type == KnifeAttack.Stab ? _stab : _swing, player, 0.5f);
        }

        public void OnPlayerHit(int attacker, int victim, int damage, HitPart part, bool kill, Vector3 victimPos)
        {
            _flashUntil[victim] = Time.time + 0.12f;
            bool mine = attacker == _m.HumanIndex;
            bool meHurt = victim == _m.HumanIndex;

            if (mine)
            {
                _ui.PlayOneShot(part == HitPart.Head ? _head : _hit, 0.9f);
                if (kill) _ui.PlayOneShot(_kill, 0.8f);
                AddPopup(victimPos + Vector3.up * (part == HitPart.Head ? 1.9f : 1.3f), damage.ToString(),
                    part == HitPart.Head ? new Color(1f, 0.85f, 0.2f) : Color.white);
            }
            else if (meHurt)
            {
                _ui.PlayOneShot(_hurt, 0.9f);
                HurtAmount = Mathf.Min(1f, HurtAmount + damage / 60f);
                if (kill) _ui.PlayOneShot(_death, 0.8f);
            }
            else
            {
                AudioSource.PlayClipAtPoint(part == HitPart.Head ? _head : _hit, victimPos + Vector3.up, (0.6f) * GameSettings.Sfx);
            }
        }

        public void OnMonsterHit(int attacker, Vector3 pos, int damage)
        {
            if (attacker != _m.HumanIndex) return;
            _ui.PlayOneShot(_monsterHit, 0.7f);
            AddPopup(pos + Vector3.up * 1.2f, damage.ToString(), new Color(0.75f, 0.85f, 1f));
        }

        public void OnMonsterDamagedPlayer(int victim, int damage, bool kill)
        {
            _flashUntil[victim] = Time.time + 0.08f;
            if (victim != _m.HumanIndex) return;
            _ui.PlayOneShot(_hurt, 0.5f);
            HurtAmount = Mathf.Min(1f, HurtAmount + damage / 80f);
            if (kill) _ui.PlayOneShot(_death, 0.8f);
        }

        public void OnSummon(int player)
        {
            Play(_summon, player, 0.45f);
        }

        public void OnPhase(RoundPhase phase, int winner, byte myTeam)
        {
            switch (phase)
            {
                case RoundPhase.Select: _ui?.PlayOneShot(_beep, 0.6f); break;
                case RoundPhase.Live: _ui?.PlayOneShot(_go, 0.8f); break;
                case RoundPhase.RoundEnd:
                case RoundPhase.MatchEnd:
                    if (winner >= 0) _ui?.PlayOneShot(winner == myTeam ? _win : _lose, 0.8f);
                    break;
            }
        }

        void Play(AudioClip clip, int player, float vol)
        {
            if (player == _m.HumanIndex) _ui.PlayOneShot(clip, vol);
            else AudioSource.PlayClipAtPoint(clip, _m.Avatars[player].transform.position + Vector3.up * 1.4f, (vol) * GameSettings.Sfx);
        }

        void AddPopup(Vector3 pos, string text, Color c)
        {
            _popups.Add(new Popup { Pos = pos, Text = text, Color = c, Born = Time.time });
            if (_popups.Count > 24) _popups.RemoveAt(0);
        }

        // ================================================================

        void LateUpdate()
        {
            HurtAmount = Mathf.MoveTowards(HurtAmount, 0f, Time.deltaTime * 1.2f);

            // 피격 반짝임 (흰색으로 잠깐)
            for (int p = 0; p < MatchController.MaxPlayers; p++)
            {
                var a = _m.Avatars[p];
                if (a == null || a.Body == null) continue;
                bool flash = Time.time < _flashUntil[p];
                if (flash)
                {
                    _mpb.SetColor("_Color", Color.white);
                    _mpb.SetColor("_EmissionColor", new Color(0.8f, 0.8f, 0.8f));
                    a.Body.SetPropertyBlock(_mpb);
                }
                else
                {
                    a.Body.SetPropertyBlock(null);
                }
            }
        }

        void OnGUI()
        {
            SkinSet.ApplyFont();
            var cam = _m.HumanCamera;
            if (cam == null) return;
            if (_popupStyle == null)
            {
                _popupStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            }

            // 피격 화면
            if (HurtAmount > 0.01f)
            {
                var old = GUI.color;
                GUI.color = new Color(0.8f, 0f, 0f, HurtAmount * 0.6f);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _vignette);
                GUI.color = old;
            }

            // 데미지 숫자 (위로 떠오르며 사라짐)
            for (int i = _popups.Count - 1; i >= 0; i--)
            {
                var p = _popups[i];
                float age = Time.time - p.Born;
                if (age > 0.9f) { _popups.RemoveAt(i); continue; }
                Vector3 sp = cam.WorldToScreenPoint(p.Pos + Vector3.up * age * 0.6f);
                if (sp.z <= 0f) continue;
                var c = p.Color;
                c.a = 1f - age / 0.9f;
                _popupStyle.normal.textColor = new Color(0, 0, 0, c.a * 0.7f);
                GUI.Label(new Rect(sp.x - 40 + 2, Screen.height - sp.y - 15 + 2, 80, 30), p.Text, _popupStyle);
                _popupStyle.normal.textColor = c;
                GUI.Label(new Rect(sp.x - 40, Screen.height - sp.y - 15, 80, 30), p.Text, _popupStyle);
            }
        }

        // ================================================================
        // 절차적 사운드 · 텍스처
        // ================================================================

        void BuildClips()
        {
            var rng = new System.Random(7);
            float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);

            _swing = Clip("swing", 0.2f, (t, s) =>
            {
                float env = Mathf.Sin(Mathf.Clamp01(t / 0.2f) * Mathf.PI);
                s.Lp = Mathf.Lerp(s.Lp, Noise(), Mathf.Lerp(0.05f, 0.35f, t / 0.2f));
                return s.Lp * env * 1.6f;
            });
            _stab = Clip("stab", 0.16f, (t, s) =>
            {
                float env = Mathf.Exp(-t * 18f);
                s.Lp = Mathf.Lerp(s.Lp, Noise(), 0.25f);
                return s.Lp * env * 1.4f + Mathf.Sin(t * 2f * Mathf.PI * 300f) * env * 0.2f;
            });
            _hit = Clip("hit", 0.16f, (t, s) =>
            {
                float env = Mathf.Exp(-t * 26f);
                return (Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(180f, 70f, t / 0.16f)) * 0.9f + Noise() * 0.35f) * env;
            });
            _head = Clip("head", 0.3f, (t, s) =>
            {
                float env = Mathf.Exp(-t * 14f);
                return (Mathf.Sin(t * 2f * Mathf.PI * 1850f) * 0.5f + Mathf.Sin(t * 2f * Mathf.PI * 2780f) * 0.3f) * env
                       + Mathf.Sin(t * 2f * Mathf.PI * 120f) * Mathf.Exp(-t * 30f) * 0.6f;
            });
            _kill = Clip("kill", 0.32f, (t, s) =>
            {
                float f = t < 0.12f ? 660f : 990f;
                float env = t < 0.12f ? 1f : Mathf.Exp(-(t - 0.12f) * 10f);
                return Mathf.Sin(t * 2f * Mathf.PI * f) * env * 0.45f;
            });
            _hurt = Clip("hurt", 0.22f, (t, s) =>
            {
                float env = Mathf.Exp(-t * 16f);
                s.Lp = Mathf.Lerp(s.Lp, Noise(), 0.08f);
                return (Mathf.Sin(t * 2f * Mathf.PI * 85f) + s.Lp * 1.5f) * env * 0.8f;
            });
            _death = Clip("death", 0.6f, (t, s) =>
            {
                float env = Mathf.Exp(-t * 4f);
                return Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(300f, 90f, t / 0.6f)) * env * 0.5f;
            });
            _monsterHit = Clip("mhit", 0.12f, (t, s) =>
            {
                float env = Mathf.Exp(-t * 30f);
                return (Mathf.Sin(t * 2f * Mathf.PI * 240f) * 0.6f + Noise() * 0.4f) * env;
            });
            _summon = Clip("summon", 0.45f, (t, s) =>
            {
                float env = Mathf.Sin(Mathf.Clamp01(t / 0.45f) * Mathf.PI);
                float f = Mathf.Lerp(180f, 520f, t / 0.45f);
                return Mathf.Sin(t * 2f * Mathf.PI * f) * (0.6f + 0.4f * Mathf.Sin(t * 60f)) * env * 0.35f;
            });
            _beep = Clip("beep", 0.12f, (t, s) => Mathf.Sin(t * 2f * Mathf.PI * 880f) * Mathf.Exp(-t * 12f) * 0.4f);
            _go = Clip("go", 0.4f, (t, s) =>
            {
                float f = t < 0.15f ? 880f : 1320f;
                return Mathf.Sin(t * 2f * Mathf.PI * f) * Mathf.Exp(-(t % 0.15f) * 8f) * 0.4f;
            });
            _win = Arp("win", new[] { 523f, 659f, 784f, 1047f });
            _lose = Arp("lose", new[] { 784f, 659f, 523f, 392f });
        }

        sealed class SynthState { public float Lp; }

        static AudioClip Clip(string name, float dur, Func<float, SynthState, float> f)
        {
            const int sr = 44100;
            int n = Mathf.CeilToInt(dur * sr);
            var data = new float[n];
            var st = new SynthState();
            for (int i = 0; i < n; i++) data[i] = Mathf.Clamp(f(i / (float)sr, st), -1f, 1f);
            var clip = AudioClip.Create(name, n, 1, sr, false);
            clip.SetData(data, 0);
            return clip;
        }

        static AudioClip Arp(string name, float[] notes)
        {
            float step = 0.13f;
            return Clip(name, step * notes.Length + 0.3f, (t, s) =>
            {
                int k = Mathf.Min(notes.Length - 1, (int)(t / step));
                float lt = t - k * step;
                return Mathf.Sin(t * 2f * Mathf.PI * notes[k]) * Mathf.Exp(-lt * 6f) * 0.35f;
            });
        }

        void BuildVignette()
        {
            const int n = 128;
            _vignette = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x / (n - 1f)) * 2f - 1f, dy = (y / (n - 1f)) * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / 1.414f;
                px[y * n + x] = new Color(1, 1, 1, Mathf.SmoothStep(0.35f, 1f, d));
            }
            _vignette.SetPixels(px);
            _vignette.Apply();
        }
    }
}
