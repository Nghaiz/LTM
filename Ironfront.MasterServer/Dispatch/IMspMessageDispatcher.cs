using System;
using Ironfront.MasterServer.Net;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer.Dispatch
{
    public interface IMspMessageDispatcher
    {
        void Dispatch(ClientConnection connection, MspMessageType messageType, ReadOnlySpan<byte> body);
        void OnDisconnected(ClientConnection connection);
        void Tick(long nowUnixMs);
    }
}
