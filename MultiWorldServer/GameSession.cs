﻿using System.Collections.Generic;
using MultiWorldLib.Messaging.Definitions.Messages;

namespace MultiWorldServer
{
    class GameSession
    {
        private int randoId;
        private Dictionary<int, PlayerSession> players;
        private Dictionary<int, string> nicknames;
        private Dictionary<int, int[]> playersCharmsNotchCosts;

        // These are to try to prevent items being lost. When items are sent, they go to unconfirmed. Once the confirmation message is received,
        // they are moved to unsaved items. When we receive a message letting us know that 
        private Dictionary<int, HashSet<MWItemReceiveMessage>> unconfirmedItems;
        private Dictionary<int, HashSet<MWItemReceiveMessage>> unsavedItems;

        public GameSession(int id)
        {
            randoId = id;
            players = new Dictionary<int, PlayerSession>();
            nicknames = new Dictionary<int, string>();
            unconfirmedItems = new Dictionary<int, HashSet<MWItemReceiveMessage>>();
            unsavedItems = new Dictionary<int, HashSet<MWItemReceiveMessage>>();
            playersCharmsNotchCosts = new Dictionary<int, int[]>();
        }

        // We know that the client received the message, but until the game is saved we can't be sure it isn't lost in a crash
        public void ConfirmItem(int playerId, MWItemReceiveMessage msg)
        {
            unconfirmedItems.GetOrCreateDefault(playerId).Remove(msg);
            unsavedItems.GetOrCreateDefault(playerId).Add(msg);
            Server.Log($"Confirmed {msg.Item} to {playerId + 1}. Unconfirmed: {unconfirmedItems[playerId].Count} Unsaved: {unsavedItems[playerId].Count}", randoId);
        }

        // If items have been both confirmed and the player saves and we STILL lose the item, they didn't deserve it anyway
        public void Save(int playerId)
        {
            if (!unsavedItems.ContainsKey(playerId)) return;
            Server.Log($"Player {playerId + 1} saved. Clearing {unsavedItems[playerId].Count} items", randoId);
            unsavedItems[playerId].Clear();
        }

        public void AddPlayer(Client c, MWJoinMessage join)
        {
            // If a player disconnects and rejoins before they can be removed from game session, you can have a weird order of events
            if (players.ContainsKey(join.PlayerId))
            {
                // In this case, make sure that their unsaved items from before are protected
                if (unsavedItems.ContainsKey(join.PlayerId))
                {
                    unconfirmedItems.GetOrCreateDefault(join.PlayerId).UnionWith(unsavedItems[join.PlayerId]);
                    unsavedItems[join.PlayerId].Clear();
                }
            }

            if (!nicknames.ContainsKey(join.PlayerId))
                nicknames[join.PlayerId] = join.DisplayName;

            PlayerSession session = new PlayerSession(join.DisplayName, join.RandoId, join.PlayerId, c.UID);
            players[join.PlayerId] = session;
            c.Session = session;

            Server.Log($"Player {join.PlayerId + 1} joined session {join.RandoId}", randoId);

            if (unconfirmedItems.ContainsKey(join.PlayerId))
            {
                foreach (var msg in unconfirmedItems[join.PlayerId])
                {
                    Server.Log($"Resending {msg.Item} to {join.PlayerId + 1} on join", randoId);
                    players[join.PlayerId].QueueConfirmableMessage(msg);
                }
            }

            if (!playersCharmsNotchCosts.ContainsKey(join.PlayerId))
            {
                players[join.PlayerId].QueueConfirmableMessage(new MWRequestCharmNotchCostsMessage());
            }

            foreach (var kvp in playersCharmsNotchCosts)
            {
                if (kvp.Key == join.PlayerId) continue;
                session.QueueConfirmableMessage(new MWAnnounceCharmNotchCostsMessage { PlayerID = kvp.Key, Costs = kvp.Value });
            }
        }

        public void RemovePlayer(Client c)
        {
            if (!players.ContainsKey(c.Session.playerId)) return;

            // See above in add player, if someone disconnects and rejoins before RemovePlayer is called, then their new session will get removed and they
            // will be in a weird limbo state. So, if the connection associated with this session doesn't match, then don't remove the player, since it
            // was on a new connection
            if (c.UID != players[c.Session.playerId].uid)
            {
                Server.Log($"Trying to remove player {c.Session.playerId + 1} but UIDs mismatch ({c.UID} != {players[c.Session.playerId].uid}). Stale connection?", randoId);
                return;
            }
            Server.Log($"Player {c.Session.playerId + 1} removed from session {c.Session.randoId}", randoId);
            players.Remove(c.Session.playerId);

            // If there are unsaved items when player is leaving, copy them to unconfirmed to be resent later
            if (unsavedItems.ContainsKey(c.Session.playerId))
            {
                unconfirmedItems.GetOrCreateDefault(c.Session.playerId).UnionWith(unsavedItems[c.Session.playerId]);
                unsavedItems[c.Session.playerId].Clear();
            }
        }

        public bool isEmpty()
        {
            return players.Count == 0 && AreDictionaryValuesEmpty(unconfirmedItems) && AreDictionaryValuesEmpty(unsavedItems);
        }

        private bool AreDictionaryValuesEmpty(Dictionary<int, HashSet<MWItemReceiveMessage>> dict)
        {
            foreach (var hashset in dict.Values)
                if (hashset.Count != 0)
                    return false;

            return true;
        }

        public void SendItemTo(int player, string item, string location, string from)
        {
            MWItemReceiveMessage msg = new MWItemReceiveMessage { Location = location, From = from, Item = item };
            if (players.ContainsKey(player))
            {
                Server.Log($"Sending item '{item}' from {from} to '{players[player].Name}'", randoId);

                players[player].QueueConfirmableMessage(msg);
            }

            // Always add to unconfirmed, which doubles as holding items for offline players
            unconfirmedItems.GetOrCreateDefault(player).Add(msg);
        }

        public string getPlayerString()
        {
            if (players.Count == 0) return "";

            string playerString = "";
            foreach (var kvp in players)
            {
                playerString += $"{kvp.Key + 1}: {kvp.Value.Name}, ";
            }

            return playerString.Substring(0, playerString.Length - 2);
        }

        internal void SendItemTo(int to, string item, string location, int playerId)
        {
            SendItemTo(to, item, location, nicknames[playerId]);
        }

        internal void AnnouncePlayerCharmNotchCosts(int playerId, MWAnnounceCharmNotchCostsMessage message)
        {
            playersCharmsNotchCosts[playerId] = message.Costs;
            foreach (var kvp in players)
            {
                if (kvp.Key == playerId) continue;

                kvp.Value.QueueConfirmableMessage(message);
            }
        }
    }
}
