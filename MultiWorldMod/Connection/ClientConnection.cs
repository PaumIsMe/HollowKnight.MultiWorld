﻿using System;
using System.Collections.Generic;
using MultiWorldLib.Binary;
using MultiWorldLib.Messaging;
using MultiWorldLib.Messaging.Definitions.Messages;
using MultiWorldMod.Connection;
using System.Net.Sockets;
using System.Threading;
using Modding;
using static MultiWorldMod.LogHelper;

using System.Net;
using MultiWorldLib;
using System.Linq;

namespace MultiWorldMod
{
    public class ClientConnection
    {
        private const int PING_INTERVAL = 10000;
        private const int WAIT_FOR_RESULT_TIMEOUT = 60 * 1000;

        private readonly MWMessagePacker Packer = new MWMessagePacker(new BinaryMWMessageEncoder());
        private TcpClient _client;
        private Timer PingTimer;
        private readonly ConnectionState State;
        private readonly List<MWItemSendMessage> ItemSendQueue = new List<MWItemSendMessage>();
        private Thread ReadThread;

        private readonly object serverResponse = new object();

        // TODO use these to make this class nicer
        public delegate void DisconnectEvent();
        public delegate void ConnectEvent(ulong uid);
        public delegate void JoinEvent();
        public delegate void LeaveEvent();

        public Action<int, string> ReadyConfirmReceived;
        public event DisconnectEvent OnDisconnect;
        public event ConnectEvent OnConnect;
        public event JoinEvent OnJoin;
        public event LeaveEvent OnLeave;

        private readonly List<MWMessage> messageEventQueue = new List<MWMessage>();

        public ClientConnection()
        {
            State = new ConnectionState();
            
            ModHooks.Instance.HeroUpdateHook += SynchronizeEvents;
        }

        public void Connect()
        {
            if (_client != null && _client.Connected)
            {
                Disconnect();
            }

            Reconnect();
        }

        private void Reconnect()
        {
            if (_client != null && _client.Connected)
            {
                return;
            }

            Log($"Attempting to connect to server");

            State.Uid = 0;
            State.LastPing = DateTime.Now;

            _client = new TcpClient
            {
                ReceiveTimeout = 2000,
                SendTimeout = 2000
            };

            if (!TryConnect())
            {
                throw new Exception($"Could not connect to {MultiWorldMod.Instance.MultiWorldSettings.URL}");
            }

            if (ReadThread != null && ReadThread.IsAlive)
            {
                ReadThread.Abort();
            }

            // Make sure we never have more than one ping timer
            PingTimer?.Dispose();
            PingTimer = new Timer(DoPing, State, 1000, PING_INTERVAL);

            ReadThread = new Thread(ReadWorker);
            ReadThread.Start();

            SendMessage(new MWConnectMessage());
        }

        private bool TryConnect()
        {
            List<string> ips = ResolveURL();
            foreach (string ip in ips)
            {
                try
                {
                    Log($"Attemping to connect to {ip}");
                    _client.Connect(ip, MultiWorldMod.Instance.MultiWorldSettings.Port);
                }
                catch { } // Ignored exception as we may connect to another IP successfully
            }
            return _client.Connected;
        }

        private List<string> ResolveURL()
        {
            List<string> ips = new List<string>();
            string url = MultiWorldMod.Instance.MultiWorldSettings.URL;

            if (IPAddress.TryParse(url, out IPAddress ip))
            {
                ips.Add(url);
            }
            else
            {
                IPHostEntry hostEntry = Dns.GetHostEntry(url);
                Array.ForEach<IPAddress>(hostEntry.AddressList, ipAddress => ips.Add(ipAddress.ToString()));
            }

            return ips;
        }

        public void JoinRando(int randoId, int playerId)
        {
            Log("Joining rando session");
            Log(MultiWorldMod.Instance.MultiWorldSettings.UserName);
            Log(randoId);
            Log(playerId);

            State.SessionId = randoId;
            State.PlayerId = playerId;

            SendMessage(new MWJoinMessage
            {
                DisplayName = MultiWorldMod.Instance.MultiWorldSettings.UserName,
                RandoId = randoId,
                PlayerId = playerId
            });
        }

        public void Rejoin()
        {
            if (State.SessionId == -1 || State.PlayerId == -1) return;
            JoinRando(State.SessionId, State.PlayerId);
        }

        public void Leave()
        {
            State.SessionId = -1;
            State.PlayerId = -1;

            State.Joined = false;
            SendMessage(new MWLeaveMessage());
            OnLeave?.Invoke();
        }
        public void Disconnect()
        {
            Log("Disconnecting from server");
            PingTimer?.Dispose();
            PingTimer = null;

            try
            {
                ReadThread?.Abort();
                ReadThread = null;
                Log($"Disconnecting (UID = {State.Uid})");
                byte[] buf = Packer.Pack(new MWDisconnectMessage { SenderUid = State.Uid }).Buffer;
                _client?.GetStream().Write(buf, 0, buf.Length);
                _client?.Close();
            }
            catch (Exception e)
            {
                Log("Error disconnection:\n" + e);
            }
            finally
            {
                State.Connected = false;
                State.Joined = false;
                _client = null;
                messageEventQueue.Clear();

                OnDisconnect?.Invoke();
            }
        }

        private void SynchronizeEvents()
        {
            MWMessage message = null;

            lock (messageEventQueue)
            {
                if (messageEventQueue.Count > 0)
                {
                    message = messageEventQueue[0];
                    messageEventQueue.RemoveAt(0);
                }
            }

            if (message == null)
            {
                return;
            }

            switch (message)
            {
                case MWItemReceiveMessage item:
                    GiveItem.HandleReceivedItem(item);
                    break;
                default:
                    Log("Unknown type in message queue: " + message.MessageType);
                    break;
            }
        }

        private void DoPing(object state)
        {
            if (_client == null || !_client.Connected)
            {
                if (State.Connected)
                {
                    State.Connected = false;
                    State.Joined = false;

                    Log("Disconnected from server");
                }

                Disconnect();
                Reconnect();
                Rejoin();
            }

            if (State.Connected)
            {
                if (DateTime.Now - State.LastPing > TimeSpan.FromMilliseconds(PING_INTERVAL * 3.5))
                {
                    Log("Connection timed out");

                    Disconnect();
                    Reconnect();
                    Rejoin();
                }
                else
                {
                    SendMessage(new MWPingMessage());
                    //If there are items in the queue that the server hasn't confirmed yet
                    if (ItemSendQueue.Count > 0 && State.Joined)
                    {
                        ResendItemQueue();
                    }
                }
            }
        }

        private void SendMessage(MWMessage msg)
        {
            try
            {
                //Always set Uid in here, if uninitialized will be 0 as required.
                //Otherwise less work resuming session etc.
                msg.SenderUid = State.Uid;
                byte[] bytes = Packer.Pack(msg).Buffer;
                NetworkStream stream = _client.GetStream();
                stream.BeginWrite(bytes, 0, bytes.Length, WriteToServer, stream);
            }
            catch (Exception e)
            {
                Log($"Failed to send message '{msg}' to server:\n{e}");
            }
        }

        private void ReadWorker()
        {
            NetworkStream stream = _client.GetStream();
            while (true)
            {
                var message = new MWPackedMessage(stream);
                ReadFromServer(message);
            }
        }

        private void WriteToServer(IAsyncResult res)
        {
            NetworkStream stream = (NetworkStream)res.AsyncState;
            stream.EndWrite(res);
        }

        private void ReadFromServer(MWPackedMessage packed)
        {
            MWMessage message;
            try
            {
                message = Packer.Unpack(packed);
            }
            catch (Exception e)
            {
                Log(e);
                return;
            }

            switch (message.MessageType)
            {
                case MWMessageType.SharedCore:
                    break;
                case MWMessageType.ConnectMessage:
                    HandleConnect((MWConnectMessage)message);
                    break;
                case MWMessageType.ReconnectMessage:
                    break;
                case MWMessageType.DisconnectMessage:
                    HandleDisconnectMessage((MWDisconnectMessage)message);
                    break;
                case MWMessageType.JoinMessage:
                    break;
                case MWMessageType.JoinConfirmMessage:
                    HandleJoinConfirm((MWJoinConfirmMessage)message);
                    break;
                case MWMessageType.LeaveMessage:
                    HandleLeaveMessage((MWLeaveMessage)message);
                    break;
                case MWMessageType.ItemReceiveMessage:
                    HandleItemReceive((MWItemReceiveMessage)message);
                    break;
                case MWMessageType.ItemReceiveConfirmMessage:
                    break;
                case MWMessageType.ItemSendMessage:
                    break;
                case MWMessageType.ItemSendConfirmMessage:
                    HandleItemSendConfirm((MWItemSendConfirmMessage)message);
                    break;
                case MWMessageType.ItemsSendConfirmMessage:
                    HandleItemsSendConfirm((MWItemsSendConfirmMessage)message);
                    break;
                case MWMessageType.NotifyMessage:
                    HandleNotify((MWNotifyMessage)message);
                    break;
                case MWMessageType.PingMessage:
                    State.LastPing = DateTime.Now;
                    break;
                case MWMessageType.ReadyConfirmMessage:
                    HandleReadyConfirm((MWReadyConfirmMessage)message);
                    break;
                case MWMessageType.RequestRandoMessage:
                    HandleRequestRando((MWRequestRandoMessage)message);
                    break;
                case MWMessageType.ResultMessage:
                    HandleResult((MWResultMessage)message);
                    break;
                case MWMessageType.RequestCharmNotchCostsMessage:
                    HandleRequestCharmNotchCosts((MWRequestCharmNotchCostsMessage)message);
                    break;
                case MWMessageType.AnnounceCharmNotchCostsMessage:
                    HandleAnnounceCharmNotchCosts((MWAnnounceCharmNotchCostsMessage)message);
                    break;
                case MWMessageType.InvalidMessage:
                default:
                    throw new InvalidOperationException("Received Invalid Message Type");
            }
        }

        private void ResendItemQueue()
        {
            foreach (MWItemSendMessage message in ItemSendQueue)
            {
                SendMessage(message);
            }
        }

        private void ClearFromSendQueue(int playerId, string item)
        {
            for (int i = ItemSendQueue.Count - 1; i >= 0; i--)
            {
                var queueItem = ItemSendQueue[i];
                if (playerId == queueItem.To && item == queueItem.Item)
                    ItemSendQueue.RemoveAt(i);
            }
        }

        private void HandleConnect(MWConnectMessage message)
        {
            State.Uid = message.SenderUid;
            State.Connected = true;
            Log($"Connected! (UID = {State.Uid})");
            OnConnect?.Invoke(State.Uid);
        }

        private void HandleJoinConfirm(MWJoinConfirmMessage message)
        {
            State.Joined = true;
            OnJoin?.Invoke();

            foreach (string item in MultiWorldMod.Instance.Settings.UnconfirmedItems)
            {
                // TODO use ItemChanger and GiveItem question mark
                (int playerId, string itemName) = LanguageStringManager.ExtractPlayerID(item);
                if (playerId < 0) continue;
                SendItem(MultiWorldMod.Instance.Settings.GetItemLocation(item), itemName, playerId);
            }
        }

        private void HandleLeaveMessage(MWLeaveMessage message)
        {
            State.Joined = false;
            OnLeave?.Invoke();
        }

        private void HandleDisconnectMessage(MWDisconnectMessage message)
        {
            State.Connected = false;
            State.Joined = false;
        }

        private void HandleNotify(MWNotifyMessage message)
        {
            lock (messageEventQueue)
            {
                messageEventQueue.Add(message);
            }
        }

        private void HandleReadyConfirm(MWReadyConfirmMessage message)
        {
            ReadyConfirmReceived?.Invoke(message.Ready, message.Names);
            MultiWorldMod.Instance.MultiWorldSettings.LastReadyID = message.ReadyID;
            MultiWorldMod.Instance.SaveMultiWorldSettings();
        }

        private void HandleItemReceive(MWItemReceiveMessage message)
        {
            lock (messageEventQueue)
            {
                messageEventQueue.Add(message);
            }

            //Do whatever we want to do when we get an item here, then confirm
            SendMessage(new MWItemReceiveConfirmMessage { Item = message.Item, From = message.From });
        }

        private void HandleItemSendConfirm(MWItemSendConfirmMessage message)
        {
            // Mark the item confirmed here, so if we send an item but disconnect we can be sure it will be resent when we open again
            Log($"Confirming item: {message.Item} to {message.To}");
            MultiWorldMod.Instance.Settings.MarkItemConfirmed(new MWItem(message.To, message.Item).ToString());
            ClearFromSendQueue(message.To, message.Item);
        }

        private void HandleItemsSendConfirm(MWItemsSendConfirmMessage message)
        {
            EjectMenuHandler.UpdateButton(message.ItemsCount);
        }

        public void ReadyUp(string room)
        {
            SendMessage(new MWReadyMessage { Room = room, Nickname = MultiWorldMod.Instance.MultiWorldSettings.UserName });
        }

        public void Unready()
        {
            SendMessage(new MWUnreadyMessage());
        }

        public void InitiateGame()
        {
            SendMessage(new MWInitiateGameMessage { Seed = RandomizerMod.RandomizerMod.Instance.Settings.Seed, ReadyID = MultiWorldMod.Instance.MultiWorldSettings.LastReadyID });
        }

        private void HandleRequestRando(MWRequestRandoMessage message)
        {
            Delegate[] tasks = RandomizerMod.Randomization.PostRandomizer.PostRandomizationActions.GetInvocationList();
            Action filteredTasks = null, postponedTasks = null, createActions = null;


            foreach (Delegate task in tasks)
            {
                if (task.Method.Name.Contains("Spoiler"))
                {
                    postponedTasks += () => CreateMultiWorldSpoilers();
                }
                else if (task.Method.Name == "CreateActions")
                {
                    createActions += () => task.DynamicInvoke();
                } 
                else
                {
                    filteredTasks += () => task.DynamicInvoke();
                }
            }
            
            RandomizerMod.Randomization.PostRandomizer.PostRandomizationActions = filteredTasks;
            RandomizerMod.Randomization.PostRandomizer.PostRandomizationActions += ExchangeItemsWithServer;
            RandomizerMod.Randomization.PostRandomizer.PostRandomizationActions += postponedTasks;
            RandomizerMod.Randomization.PostRandomizer.PostRandomizationActions += createActions;
            RandomizerMod.Randomization.PostRandomizer.PostRandomizationActions += 
                MultiWorldMod.Instance.NotifyRandomizationFinished;
            RandomizerMod.Randomization.PostRandomizer.PostRandomizationActions += () => 
            JoinRando(MultiWorldMod.Instance.Settings.MWRandoId, MultiWorldMod.Instance.Settings.MWPlayerId);
            RandomizerMod.Randomization.PostRandomizer.PostRandomizationActions += EjectMenuHandler.Initialize;
            
            RandomizerMod.Randomization.PostRandomizer.PostRandomizationActions += () => 
            On.GameManager.BeginSceneTransition += SetCharmNotchCostsLogicDone;

            // Start game in a different thread, allowing handling of incoming requests
            new Thread(MultiWorldMod.Instance.StartGame).Start();
        }

        private void SetCharmNotchCostsLogicDone(On.GameManager.orig_BeginSceneTransition orig, GameManager self, GameManager.SceneLoadInfo info)
        {
            CharmNotchCostsObserver.SetCharmNotchCostsLogicDone();
            On.GameManager.BeginSceneTransition -= SetCharmNotchCostsLogicDone;

            orig(self, info);
            return;
        }

        private void ExchangeItemsWithServer() 
        {
            lock (serverResponse)
            {
                // Filter out start items
                SendMessage(new MWRandoGeneratedMessage { Items =
                    Array.FindAll(RandomizerMod.Randomization.PostRandomizer.getOrderedILPairs(),
                    item => !item.Item3.StartsWith("Equipped")) });
                Monitor.Wait(serverResponse);
                Log("Exchanged items with server successfully!");
            }
        }

        private void HandleResult(MWResultMessage message)
        {
            lock (serverResponse)
            {
                MultiWorldMod.Instance.Settings.MWPlayerId = message.ResultData.playerId;
                MultiWorldMod.Instance.Settings.MWNumPlayers = message.ResultData.nicknames.Length;
                MultiWorldMod.Instance.Settings.MWRandoId = message.ResultData.randoId;
                MultiWorldMod.Instance.Settings.SetMWNames(message.ResultData.nicknames);
                MultiWorldMod.Instance.Settings.IsMW = true;
                MultiWorldMod.Instance.Settings.LastUsedSeed = RandomizerMod.RandomizerMod.Instance.Settings.Seed;

                LanguageStringManager.SetMWNames(message.ResultData.nicknames);
                ItemManager.UpdatePlayerItems(message.Items);

                SpoilerLogger.StoreItemsSpoiler(message.ResultData.ItemsSpoiler);
                SpoilerLogger.StorePlayerItems(message.ResultData.PlayerItems);

                Monitor.Pulse(serverResponse);
            }
        }

        private void CreateMultiWorldSpoilers()
        {
            try
            {
                (int, string, string)[] emptyList = new (int, string, string)[0];
                RandomizerMod.RandoLogger.LogAllToSpoiler(emptyList, 
                    RandomizerMod.RandomizerMod.Instance.Settings._transitionPlacements.Select(kvp => (kvp.Key, kvp.Value)).ToArray());

                SpoilerLogger.LogItemsSpoiler();
                SpoilerLogger.LogCondensedSpoiler();
            }
            catch (Exception e)
            {
                Log("Spoiler Logger failed " + e.Message);
                Log(e.StackTrace);
            }
        }

        public void RejoinGame()
        {
            RandomizerMod.RandomizerMod.Instance.Settings.Seed = MultiWorldMod.Instance.Settings.LastUsedSeed;
            SendMessage(new MWRejoinMessage { ReadyID = MultiWorldMod.Instance.MultiWorldSettings.LastReadyID });
        }

        public void SendItem(string loc, string item, int playerId)
        {
            Log($"Sending item {item} to {playerId}");
            MWItemSendMessage msg = new MWItemSendMessage { Location = loc, Item = item, To = playerId };
            ItemSendQueue.Add(msg);
            SendMessage(msg);
        }

        public void SendItems(List<(int, string, string)> items)
        {
            SendMessage(new MWItemsSendMessage { Items = items });
        }

        public void NotifySave()
        {
            SendMessage(new MWSaveMessage { ReadyID = MultiWorldMod.Instance.MultiWorldSettings.LastReadyID });
            MultiWorldMod.Instance.MultiWorldSettings.LastReadyID = -1;
        }

        private void HandleRequestCharmNotchCosts(MWRequestCharmNotchCostsMessage message)
        {
            if (!CharmNotchCostsObserver.IsRandomizationLogicDone()) return;

            SendMessage(new MWAnnounceCharmNotchCostsMessage {
                PlayerID = MultiWorldMod.Instance.Settings.MWPlayerId,
                Costs = CharmNotchCostsObserver.GetCharmNotchCosts()
            });
        }

        private void HandleAnnounceCharmNotchCosts(MWAnnounceCharmNotchCostsMessage message)
        {
            Log($"Received {message.PlayerID}'s charm notch costs");
            ItemManager.UpdateOthersCharmNotchCosts(message.PlayerID, message.Costs);
            SendMessage(new MWConfirmCharmNotchCostsReceivedMessage { PlayerID = message.PlayerID });
        }

        public bool IsConnected()
        {
            return State.Connected;
        }

        public ConnectionStatus GetStatus()
        {
            if (!State.Connected)
            {
                return ConnectionStatus.NotConnected;
            }

            if (!State.Joined)
            {
                return ConnectionStatus.Connected;
            }

            return ConnectionStatus.Joined;
        }

        public enum ConnectionStatus
        {
            NotConnected,
            Connected,
            Joined
        }

        ~ClientConnection()
        {
            try
            {
                Disconnect();
            }
            catch (Exception) {}
        }
    }
}
