﻿namespace MultiWorldLib.Messaging.Definitions.Messages
{
    [MWMessageType(MWMessageType.ReadyMessage)]
    public class MWReadyMessage : MWMessage
    {
        public string Room { get; set; }
        public string Nickname { get; set; }
        public MWReadyMessage()
        {
            MessageType = MWMessageType.ReadyMessage;
        }
    }

    public class MWReadyMessageDefinition : MWMessageDefinition<MWReadyMessage>
    {
        public MWReadyMessageDefinition() : base(MWMessageType.ReadyMessage)
        {
            Properties.Add(new MWMessageProperty<string, MWReadyMessage>(nameof(MWReadyMessage.Room)));
            Properties.Add(new MWMessageProperty<string, MWReadyMessage>(nameof(MWReadyMessage.Nickname)));
        }
    }
}
