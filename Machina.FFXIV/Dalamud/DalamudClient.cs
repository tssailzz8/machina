﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Network;
using Machina.FFXIV.Headers;
using System.Collections.Concurrent;
using Dalamud.Logging;
using Dalamud.Utility;
using Dalamud;
using System.Runtime.InteropServices;
using System.Reflection.Emit;

namespace Machina.FFXIV.Dalamud
{
    public class DalamudClient : IDisposable
    {
        public static GameNetwork GameNetwork { get; set; }


        public delegate void MessageReceivedHandler(long epoch, byte[] message);
        public MessageReceivedHandler MessageReceived;

        private CancellationTokenSource _tokenSource;
        private Task _monitorTask;
        private ConcurrentQueue<(long, byte[])> _messageQueue;

        private DateTime _lastLoopError;

        private readonly Dictionary<Server_MessageType, int> OpcodeSizes;

        internal unsafe DalamudClient()
        {
            OpcodeSizes = new Dictionary<Server_MessageType, int>
            {
                { Server_MessageType.StatusEffectList, sizeof(Server_StatusEffectList) },
                { Server_MessageType.StatusEffectList2, sizeof(Server_StatusEffectList2) },
                { Server_MessageType.StatusEffectList3, sizeof(Server_StatusEffectList3) },
                { Server_MessageType.BossStatusEffectList, sizeof(Server_BossStatusEffectList) },
                { Server_MessageType.Ability1, sizeof(Server_ActionEffect1) },
                { Server_MessageType.Ability8, sizeof(Server_ActionEffect8) },
                { Server_MessageType.Ability16, sizeof(Server_ActionEffect16) },
                { Server_MessageType.Ability24, sizeof(Server_ActionEffect24) },
                { Server_MessageType.Ability32, sizeof(Server_ActionEffect32) },
                { Server_MessageType.ActorCast, sizeof(Server_ActorCast) },
                { Server_MessageType.EffectResult, sizeof(Server_EffectResult) },
                { Server_MessageType.EffectResultBasic, sizeof(Server_EffectResultBasic) },
                { Server_MessageType.ActorControl, sizeof(Server_ActorControl) },
                { Server_MessageType.ActorControlSelf, sizeof(Server_ActorControlSelf) },
                { Server_MessageType.ActorControlTarget, sizeof(Server_ActorControlTarget) },
                { Server_MessageType.UpdateHpMpTp, sizeof(Server_UpdateHpMpTp) },
                { Server_MessageType.ActorGauge, sizeof(Server_ActorGauge) },
                { Server_MessageType.PresetWaymark, sizeof(Server_PresetWaymark) },
                { Server_MessageType.Waymark, sizeof(Server_Waymark) },
                { Server_MessageType.SystemLogMessage, sizeof(Server_SystemLogMessage) },
                { Server_MessageType.ActorMove, 0x30 },
                { Server_MessageType.NpcSpawn, 0x2A0 }
            };
        }


        public void OnMessageReceived(long epoch, byte[] message)
        {
            MessageReceived?.Invoke(epoch, message);
        }


        public void Connect()
        {
            if (GameNetwork is null)
            {
                Trace.WriteLine($"DalamudClient: Dalamud GameNetwork has not been injected/set.", "DEBUG-MACHINA");
                return;
            }
            _messageQueue = new ConcurrentQueue<(long, byte[])>();

            GameNetwork.NetworkMessage += GameNetworkOnNetworkMessage;

            _tokenSource = new CancellationTokenSource();

            _monitorTask = Task.Run(() => ProcessReadLoop(_tokenSource.Token));
        }

        private unsafe void ReplayNetworkOnNetworkMessage(long a, long b, long c)
        {
            var size = 0x1000;    // best effort
            var dataAdress = (IntPtr)b;
            var opcode = (ushort)Marshal.ReadInt16(dataAdress);
            var dataPtr = (IntPtr)c;
            SafeMemory.Read<UInt32>(dataAdress + 8, out var sourceID);
            // if we can't map the opcode to its true size it *should* still be fine
            if (OpcodeSizes.ContainsKey((Server_MessageType)opcode))
                size = OpcodeSizes[(Server_MessageType)opcode];

            // we don't have the original package timestamp but this seems to be close enough (+- 5ms)
            var epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            dataPtr -= 0x20;

            var stream = new UnmanagedMemoryStream((byte*)dataPtr.ToPointer(), size);
            var reader = new BinaryReader(stream);
            var message = reader.ReadBytes(size);



            fixed (byte* ptr = message) // fix up corrupted segment header with what we have
            {
                Server_MessageHeader* headerPtr = (Server_MessageHeader*)ptr;
                headerPtr->MessageLength = (uint)size;
                headerPtr->LoginUserID = sourceID;
                headerPtr->ActorID = sourceID;
                headerPtr->MessageType = opcode;

            }

            _messageQueue.Enqueue((epoch, message));

            reader.Close();
            stream.Close();
            reader.Dispose();
            stream.Dispose();
        }


        private void ProcessReadLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        while (_messageQueue.TryDequeue(out var messageInfo))
                            OnMessageReceived(messageInfo.Item1, messageInfo.Item2);

                        Task.Delay(10, token).Wait(token);
                    }
                    catch (OperationCanceledException)
                    {

                    }
                    catch (Exception ex)
                    {
                        if (DateTime.UtcNow.Subtract(_lastLoopError).TotalSeconds > 5)
                            Trace.WriteLine("DalamudClient: Error in inner ProcessReadLoop. " + ex.ToString(), "DEBUG-MACHINA");
                        _lastLoopError = DateTime.UtcNow;
                    }

                }
            }
            catch (OperationCanceledException)
            {

            }
            catch (Exception ex)
            {
                Trace.WriteLine("DalamudClient: Error in outer ProcessReadLoop. " + ex.ToString(), "DEBUG-MACHINA");
            }
        }



            protected unsafe void GameNetworkOnNetworkMessage(IntPtr dataPtr, ushort opcode, uint sourceActorId, uint targetActorId, NetworkMessageDirection direction)
        {
            if (direction != NetworkMessageDirection.ZoneDown)
                return;

            var size = 0x1000;    // best effort

            // if we can't map the opcode to its true size it *should* still be fine
            if (OpcodeSizes.ContainsKey((Server_MessageType)opcode))
                size = OpcodeSizes[(Server_MessageType)opcode];

            // we don't have the original package timestamp but this seems to be close enough (+- 5ms)
            var epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            dataPtr -= 0x20;

            var stream = new UnmanagedMemoryStream((byte*)dataPtr.ToPointer(), size);
            var reader = new BinaryReader(stream);
            var message = reader.ReadBytes(size);

            if (sourceActorId == 0) // no idea why this happens, probably a Dalamud bug
                sourceActorId = targetActorId;  // in that case targetActorId seems to be what we actually want

            fixed (byte* ptr = message) // fix up corrupted segment header with what we have
            {
                Server_MessageHeader* headerPtr = (Server_MessageHeader*)ptr;
                headerPtr->MessageLength = (uint)size;
                headerPtr->LoginUserID = targetActorId;
                headerPtr->ActorID = sourceActorId;
            }

            _messageQueue.Enqueue((epoch, message));

            reader.Close();
            stream.Close();
            reader.Dispose();
            stream.Dispose();
        }

        public void Disconnect()
        {
            _tokenSource?.Cancel();
            _tokenSource?.Dispose();
            _tokenSource = null;

            if (GameNetwork is null)
                return;

            GameNetwork.NetworkMessage -= GameNetworkOnNetworkMessage;
        }

        #region IDisposable
        protected virtual void Dispose(bool disposing)
        {
            Disconnect();
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
