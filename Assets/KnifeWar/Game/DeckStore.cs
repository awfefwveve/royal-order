using System;
using KnifeWar.Core;
using UnityEngine;

namespace KnifeWar.Game
{
    /// <summary>
    /// 내 덱 (로비에서 편집, 경기 입장 때 서버로 보낸다).
    /// 기기(PlayerPrefs)에 저장하고, 계정 서버에도 올린다 (NetBootstrap.Account 가 로그인 뒤 맞춘다).
    /// </summary>
    public static class DeckStore
    {
        const string PrefKey = "kw.deck";
        static byte[] _current;

        public static event Action Changed;

        public static byte[] Current
        {
            get
            {
                if (_current == null) Load();
                return _current;
            }
        }

        public static string Text => CardDefs.ToText(Current);

        public static void Load()
        {
            _current = CardDefs.Normalize(CardDefs.Parse(PlayerPrefs.GetString(PrefKey, "")));
        }

        /// <summary>규칙에 맞는 덱만 받는다. 저장하고 Changed 를 알린다 (계정 서버 업로드는 듣는 쪽이).</summary>
        public static bool Set(byte[] deck, bool notify = true)
        {
            if (!CardDefs.IsValidDeck(deck)) return false;
            _current = (byte[])deck.Clone();
            PlayerPrefs.SetString(PrefKey, CardDefs.ToText(_current));
            PlayerPrefs.Save();
            if (notify) Changed?.Invoke();
            return true;
        }

        /// <summary>계정 서버에서 받은 덱 (로그인 직후). 비어 있으면 그대로 둔다.</summary>
        public static void SetFromServer(string text)
        {
            var d = CardDefs.Parse(text);
            if (d != null && CardDefs.IsValidDeck(d)) Set(d, notify: false);
        }

        public static void Reset() => Set(CardDefs.DefaultDeck);
    }
}
