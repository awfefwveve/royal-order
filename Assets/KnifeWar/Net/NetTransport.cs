using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace KnifeWar.Net
{
    /// <summary>
    /// Unity Transport(UDP) 래퍼. 서버·클라 공용.
    /// - 신뢰 채널: 조각화 + 순서 보장 재전송 (이벤트·접속 메시지)
    /// - 비신뢰 채널: 그대로 (입력·스냅샷 — 최신 것만 중요)
    /// - 테스트용 지연: 보내는 패킷을 일정 시간 붙잡았다가 보낸다
    /// </summary>
    public sealed class NetTransport : IDisposable
    {
        public NetworkDriver Driver;
        public NetworkPipeline Reliable;
        public NetworkPipeline Unreliable;

        struct Pending
        {
            public NetworkConnection Conn;
            public bool Reliable;
            public byte[] Data;
            public float Due;
        }

        readonly List<Pending> _delayed = new List<Pending>();
        byte[] _recv = new byte[2048];

        public NetTransport()
        {
            var settings = new NetworkSettings();
            settings.WithFragmentationStageParameters(payloadCapacity: 256 * 1024);
            settings.WithReliableStageParameters(windowSize: 64);
            settings.WithNetworkConfigParameters(disconnectTimeoutMS: 15000, heartbeatTimeoutMS: 1000);
            Driver = NetworkDriver.Create(settings);
            Reliable = Driver.CreatePipeline(typeof(FragmentationPipelineStage), typeof(ReliableSequencedPipelineStage));
            Unreliable = NetworkPipeline.Null;
        }

        public void Dispose()
        {
            if (Driver.IsCreated) Driver.Dispose();
        }

        public void Update()
        {
            Driver.ScheduleUpdate().Complete();
            FlushDelayed();
        }

        /// <summary>보내기. delayMs > 0이면 그만큼 늦게 보낸다 (테스트용 핑).</summary>
        public void Send(NetworkConnection conn, bool reliable, ByteWriter w, float delayMs = 0f)
        {
            if (delayMs > 0f)
            {
                _delayed.Add(new Pending { Conn = conn, Reliable = reliable, Data = w.ToArray(), Due = Time.realtimeSinceStartup + delayMs / 1000f });
                return;
            }
            SendNow(conn, reliable, w.Buffer, w.Length);
        }

        void SendNow(NetworkConnection conn, bool reliable, byte[] data, int len)
        {
            if (!conn.IsCreated) return;
            int status = Driver.BeginSend(reliable ? Reliable : Unreliable, conn, out var writer);
            if (status < 0)
            {
                Debug.LogWarning($"[KnifeWar] BeginSend 실패 status={status} len={len}");
                return;
            }
            for (int i = 0; i < len; i++) writer.WriteByte(data[i]);
            if (writer.HasFailedWrites)
            {
                Driver.AbortSend(writer);
                Debug.LogWarning($"[KnifeWar] 패킷이 너무 큼 len={len}");
                return;
            }
            Driver.EndSend(writer);
        }

        void FlushDelayed()
        {
            if (_delayed.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            int i = 0;
            while (i < _delayed.Count)
            {
                var p = _delayed[i];
                if (p.Due <= now)
                {
                    SendNow(p.Conn, p.Reliable, p.Data, p.Data.Length);
                    _delayed.RemoveAt(i);
                }
                else i++;
            }
        }

        public void Flush()
        {
            Driver.ScheduleFlushSend(default).Complete();
        }

        /// <summary>받은 패킷을 byte[]로 복사해서 읽기 도구로 돌려준다.</summary>
        public ByteReader Read(ref DataStreamReader reader)
        {
            int len = reader.Length;
            if (_recv.Length < len) _recv = new byte[Math.Max(len, _recv.Length * 2)];
            for (int i = 0; i < len; i++) _recv[i] = reader.ReadByte();
            return new ByteReader(_recv, len);
        }
    }
}
