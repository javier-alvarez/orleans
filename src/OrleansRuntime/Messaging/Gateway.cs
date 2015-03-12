/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using Orleans.Runtime.Configuration;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal class Gateway
    {
        private static readonly TimeSpan TIME_BEFORE_CLIENT_DROP = TimeSpan.FromSeconds(60);

        private readonly MessageCenter messageCenter;
        private readonly GatewayAcceptor acceptor;
        private readonly Lazy<GatewaySender>[] senders;
        private readonly GatewayClientCleanupAgent dropper;

        // clients is the main authorative collection of all connected clients. 
        // Any client currently in the system appears in this collection. 
        // In addition, we use clientSockets and proxiedGrains collections for fast retrival of ClientState. 
        // Anything that appears in those 2 collections should also appear in the main clients collection.
        private readonly ConcurrentDictionary<GrainId, ClientState> clients;
        private readonly ConcurrentDictionary<Socket, ClientState> clientSockets;
        private readonly ConcurrentDictionary<GrainId, ClientState> proxiedGrains;
        private readonly SiloAddress gatewayAddress;
        private int nextGatewaySenderToUseForRoundRobin;
        private readonly ClientsReplyRoutingCache clientsReplyRoutingCache;
        private ClientObserverRegistrar clientRegistrar;
        private readonly object lockable;
        private static readonly TraceLogger logger = TraceLogger.GetLogger("Orleans.Messaging.Gateway");
        
        private IMessagingConfiguration MessagingConfiguration { get { return messageCenter.MessagingConfiguration; } }
        
        internal Gateway(MessageCenter msgCtr, IPEndPoint gatewayAddress)
        {
            messageCenter = msgCtr;
            acceptor = new GatewayAcceptor(msgCtr, this, gatewayAddress);
            senders = new Lazy<GatewaySender>[messageCenter.MessagingConfiguration.GatewaySenderQueues];
            nextGatewaySenderToUseForRoundRobin = 0;
            dropper = new GatewayClientCleanupAgent(this);
            clients = new ConcurrentDictionary<GrainId, ClientState>();
            clientSockets = new ConcurrentDictionary<Socket, ClientState>();
            proxiedGrains = new ConcurrentDictionary<GrainId, ClientState>();
            clientsReplyRoutingCache = new ClientsReplyRoutingCache(messageCenter.MessagingConfiguration);
            this.gatewayAddress = SiloAddress.New(gatewayAddress, 0);
            lockable = new object();
        }

        internal void Start(ClientObserverRegistrar clientRegistrar)
        {
            this.clientRegistrar = clientRegistrar;
            this.clientRegistrar.SetGateway(this);
            acceptor.Start();
            for (int i = 0; i < senders.Length; i++)
            {
                int capture = i;
                senders[capture] = new Lazy<GatewaySender>(() =>
                {
                    var sender = new GatewaySender("GatewaySiloSender_" + capture, this);
                    sender.Start();
                    return sender;
                }, LazyThreadSafetyMode.ExecutionAndPublication);
            }
            dropper.Start();
        }

        internal void Stop()
        {
            dropper.Stop();
            foreach (var sender in senders)
            {
                if (sender != null && sender.IsValueCreated)
                    sender.Value.Stop();
            }
            acceptor.Stop();
        }

        internal ICollection<GrainId> GetConnectedClients()
        {
            return clients.Keys;
        }

        internal void RecordOpenedSocket(Socket sock, GrainId clientId)
        {
            lock (lockable)
            {
                logger.Info(ErrorCode.GatewayClientOpenedSocket, "Recorded opened socket from endpoint {0}, client ID {1}.", sock.RemoteEndPoint, clientId);
                ClientState clientState;
                if (clients.TryGetValue(clientId, out clientState))
                {
                    var oldSocket = clientState.Socket;
                    if (oldSocket != null)
                    {
                        // The old socket will be closed by itself later.
                        ClientState ignore;
                        clientSockets.TryRemove(oldSocket, out ignore);
                    }
                    QueueRequest(clientState, null);
                }
                else
                {
                    int gatewayToUse = nextGatewaySenderToUseForRoundRobin % senders.Length;
                    nextGatewaySenderToUseForRoundRobin++; // under Gateway lock
                    clientState = new ClientState(clientId, gatewayToUse);
                    clients[clientId] = clientState;
                    MessagingStatisticsGroup.ConnectedClientCount.Increment();
                }
                clientState.RecordConnection(sock);
                clientSockets[sock] = clientState;
                clientRegistrar.ClientAdded(clientId);
                NetworkingStatisticsGroup.OnOpenedGatewayDuplexSocket();
            }
        }

        internal void RecordClosedSocket(Socket sock)
        {
            if (sock == null) return;
            lock (lockable)
            {
                ClientState cs = null;
                if (!clientSockets.TryGetValue(sock, out cs)) return;

                EndPoint endPoint = null;
                try
                {
                    endPoint = sock.RemoteEndPoint;
                }
                catch (Exception) { } // guard against ObjectDisposedExceptions
                logger.Info(ErrorCode.GatewayClientClosedSocket, "Recorded closed socket from endpoint {0}, client ID {1}.", endPoint != null ? endPoint.ToString() : "null", cs.Id);

                ClientState ignore;
                clientSockets.TryRemove(sock, out ignore);
                cs.RecordDisconnection();
            }
        }

        internal void RecordProxiedGrain(GrainId grainId, GrainId clientId)
        {
            lock (lockable)
            {
                ClientState cs;
                if (clients.TryGetValue(clientId, out cs))
                {
                    // TO DO done: what if we have an older proxiedGrain for this client?
                    // We now support many proxied grains per client, so there's no need to handle it specially here.
                    proxiedGrains.AddOrUpdate(grainId, cs, (k, v) => cs);
                }
            }
        }

        internal void RecordSendingProxiedGrain(GrainId senderGrainId, Socket clientSocket)
        {
            // not taking global lock on the crytical path!
            ClientState cs;
            if (clientSockets.TryGetValue(clientSocket, out cs))
            {
                // TO DO done: what if we have an older proxiedGrain for this client?
                // We now support many proxied grains per client, so there's no need to handle it specially here.
                proxiedGrains.AddOrUpdate(senderGrainId, cs, (k, v) => cs);
            }
        }

        internal SiloAddress TryToReroute(Message msg)
        {
            // for responses from ClientAddressableObject to ClientGrain try to use clientsReplyRoutingCache for sending replies directly back.
            if (!msg.SendingGrain.IsClientAddressableObject || !msg.TargetGrain.IsClientGrain) return null;

            if (msg.Direction != Message.Directions.Response) return null;

            SiloAddress gateway;
            return clientsReplyRoutingCache.TryFindClientRoute(msg.TargetGrain, out gateway) ? gateway : null;
        }

        internal void RecordUnproxiedGrain(GrainId id)
        {
            lock (lockable)
            {
                ClientState ignore;
                proxiedGrains.TryRemove(id, out ignore);
            }
        }

        internal void DropDisconnectedClients()
        {
            lock (lockable)
            {
                List<ClientState> clientsToDrop = clients.Values.Where(cs => cs.ReadyToDrop()).ToList();
                foreach (ClientState client in clientsToDrop)
                    DropClient(client);
            }
        }

        internal void DropExpiredRoutingCachedEntries()
        {
            lock (lockable)
            {
                clientsReplyRoutingCache.DropExpiredEntries();
            }
        }
        

        // This function is run under global lock
        // There is NO need to acquire individual ClientState lock, since we only access client Id (immutable) and close an older socket.
        private void DropClient(ClientState client)
        {
            logger.Info(ErrorCode.GatewayDroppingClient, "Dropping client {0}, {1} after disconnect with no reconnect", 
                client.Id, DateTime.UtcNow.Subtract(client.DisconnectedSince));

            ClientState ignore;
            clients.TryRemove(client.Id, out ignore);
            clientRegistrar.ClientDropped(client.Id);

            Socket oldSocket = client.Socket;
            if (oldSocket != null)
            {
                // this will not happen, since we drop only already disconnected clients, for socket is already null. But leave this code just to be sure.
                client.RecordDisconnection();
                clientSockets.TryRemove(oldSocket, out ignore);
                SocketManager.CloseSocket(oldSocket);
            }

            List<GrainId> proxies = proxiedGrains.Where((KeyValuePair<GrainId, ClientState> pair) => pair.Value.Id.Equals(client.Id)).Select(p => p.Key).ToList();
            foreach (GrainId proxy in proxies)
                proxiedGrains.TryRemove(proxy, out ignore);
            
            MessagingStatisticsGroup.ConnectedClientCount.DecrementBy(1);
            messageCenter.RecordClientDrop(proxies);
        }

        /// <summary>
        /// See if this message is intended for a grain we're proxying, and queue it for delivery if so.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>true if the message should be delivered to a proxied grain, false if not.</returns>
        internal bool TryDeliverToProxy(Message msg)
        {
            // See if it's a grain we're proxying.
            ClientState client;
            
            // not taking global lock on the crytical path!
            if (!proxiedGrains.TryGetValue(msg.TargetGrain, out client))
                return false;
            
            if (!clients.ContainsKey(client.Id))
            {
                lock (lockable)
                {
                    if (!clients.ContainsKey(client.Id))
                    {
                        ClientState ignore;
                        // Lazy clean-up for dropped clients
                        proxiedGrains.TryRemove(msg.TargetGrain, out ignore);
                        // I don't think this can ever happen. When we drop the client (the only place we remove the ClientState from clients collection)
                        // we also actively remove all proxiedGrains for this client. So the clean-up will be non lazy.
                        // leaving it for now.
                        return false;
                    }
                }
            }

            // when this Gateway receives a message from client X to client addressale object Y
            // it needs to record the original Gateway address through which this message came from (the address of the Gateway that X is connected to)
            // it will use this Gateway to re-route the REPLY from Y back to X.
            if (msg.SendingGrain.IsClientGrain && msg.TargetGrain.IsClientAddressableObject)
            {
                clientsReplyRoutingCache.RecordClientRoute(msg.SendingGrain, msg.SendingSilo);
            }
            
            msg.TargetSilo = null;
            msg.SendingSilo = gatewayAddress; // This makes sure we don't expose wrong silo addresses to the client. Client will only see silo address of the Gateway it is connected to.
            QueueRequest(client, msg);
            return true;
        }

        private void QueueRequest(ClientState clientState, Message msg)
        {
            //int index = senders.Length == 1 ? 0 : Math.Abs(clientId.GetHashCode()) % senders.Length;
            int index = clientState.GatewaySenderNumber;
            senders[index].Value.QueueRequest(new OutgoingClientMessage(clientState.Id, msg));   
        }

        internal void SendMessage(Message msg)
        {
            messageCenter.SendMessage(msg);
        }


        private class ClientState
        {
            internal Queue<Message> PendingToSend { get; private set; }
            internal Queue<List<Message>> PendingBatchesToSend { get; private set; }
            internal Socket Socket { get; private set; }
            internal DateTime DisconnectedSince { get; private set; }
            internal GrainId Id { get; private set; }
            internal int GatewaySenderNumber { get; private set; }

            internal bool IsConnected { get { return Socket != null; } }

            internal ClientState(GrainId id, int gatewaySenderNumber)
            {
                Id = id;
                GatewaySenderNumber = gatewaySenderNumber;
                PendingToSend = new Queue<Message>();
                PendingBatchesToSend = new Queue<List<Message>>();
            }

            internal void RecordDisconnection()
            {
                if (Socket == null) return;

                DisconnectedSince = DateTime.UtcNow;
                Socket = null;
                NetworkingStatisticsGroup.OnClosedGatewayDuplexSocket();
            }

            internal void RecordConnection(Socket sock)
            {
                Socket = sock;
                DisconnectedSince = DateTime.MaxValue;
            }

            internal bool ReadyToDrop()
            {
                return !IsConnected &&
                       (DateTime.UtcNow.Subtract(DisconnectedSince) >= Gateway.TIME_BEFORE_CLIENT_DROP);
            }
        }


        private class GatewayClientCleanupAgent : AsynchAgent
        {
            private readonly Gateway gateway;

            internal GatewayClientCleanupAgent(Gateway gateway)
            {
                this.gateway = gateway;
            }

            #region Overrides of AsynchAgent

            protected override void Run()
            {
                while (!Cts.IsCancellationRequested)
                {
                    gateway.DropDisconnectedClients();
                    gateway.DropExpiredRoutingCachedEntries();
                    Thread.Sleep(TIME_BEFORE_CLIENT_DROP);
                }
            }

            #endregion
        }

        // this cache is used to record the addresses of Gateways from which clients connected to.
        // it is used to route replies to clients from client addressable objects
        // without this cache this Gateway will not know how to route the reply back to the client 
        // (since clients are not registered in the directory and this Gateway may not be proxying for the client for whom the reply is destined).
        private class ClientsReplyRoutingCache
        {
            // for every client: the Gateway to use to route repies back to it plus the last time that client connected via this Gateway.
            private readonly ConcurrentDictionary<GrainId, Tuple<SiloAddress, DateTime>> clientRoutes;
            private readonly TimeSpan TIME_BEFORE_ROUTE_CACHED_ENTRY_EXPIRES;

            internal ClientsReplyRoutingCache(IMessagingConfiguration messagingConfiguration)
            {
                clientRoutes = new ConcurrentDictionary<GrainId, Tuple<SiloAddress, DateTime>>();
                TIME_BEFORE_ROUTE_CACHED_ENTRY_EXPIRES = messagingConfiguration.ResponseTimeout.Multiply(5);
            }

            internal void RecordClientRoute(GrainId client, SiloAddress gateway)
            {
                var now = DateTime.UtcNow;
                clientRoutes.AddOrUpdate(client, new Tuple<SiloAddress, DateTime>(gateway, now), (k, v) => new Tuple<SiloAddress, DateTime>(gateway, now));
            }

            internal bool TryFindClientRoute(GrainId client, out SiloAddress gateway)
            {
                gateway = null;
                Tuple<SiloAddress, DateTime> tuple;
                bool ret = clientRoutes.TryGetValue(client, out tuple);
                if (ret)
                    gateway = tuple.Item1;

                return ret;
            }

            internal void DropExpiredEntries()
            {
                List<GrainId> clientsToDrop = clientRoutes.Where(route => Expired(route.Value.Item2)).Select(kv => kv.Key).ToList();
                foreach (GrainId client in clientsToDrop)
                {
                    Tuple<SiloAddress, DateTime> tuple;
                    clientRoutes.TryRemove(client, out tuple);
                }
            }

            private bool Expired(DateTime lastUsed)
            {
                return DateTime.UtcNow.Subtract(lastUsed) >= TIME_BEFORE_ROUTE_CACHED_ENTRY_EXPIRES;
            }
        }
        
        
        private class GatewaySender : AsynchQueueAgent<OutgoingClientMessage>
        {
            private readonly Gateway gateway;
            private readonly CounterStatistic gatewaySends;

            internal GatewaySender(string name, Gateway gateway)
                : base(name, gateway.MessagingConfiguration)
            {
                this.gateway = gateway;
                gatewaySends = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_SENT);
                OnFault = FaultBehavior.RestartOnFault;
            }

            protected override void Process(OutgoingClientMessage request)
            {
                if (Cts.IsCancellationRequested) return;
                
                var client = request.Item1;
                var msg = request.Item2;

                // Find the client state
                ClientState clientState;
                bool found;
                lock (gateway.lockable)
                {
                    found = gateway.clients.TryGetValue(client, out clientState);
                }

                // This should never happen -- but make sure to handle it reasonably, just in case
                if (!found || (clientState == null))
                {
                    if (msg == null) return;

                    Log.Info(ErrorCode.GatewayTryingToSendToUnrecognizedClient, "Trying to send a message {0} to an unrecognized client {1}", msg.ToString(), client);
                    MessagingStatisticsGroup.OnFailedSentMessage(msg);
                    // Message for unrecognized client -- reject it
                    if (msg.Direction == Message.Directions.Request)
                    {
                        MessagingStatisticsGroup.OnRejectedMessage(msg);
                        Message error = msg.CreateRejectionResponse(Message.RejectionTypes.Unrecoverable, "Unknown client " + client);
                        gateway.SendMessage(error);
                    }
                    else
                    {
                        MessagingStatisticsGroup.OnDroppedSentMessage(msg);
                    }
                    return;
                }

                // if disconnected - queue for later.
                if (!clientState.IsConnected)
                {
                    if (msg == null) return;

                    if (Log.IsVerbose3) Log.Verbose3("Queued message {0} for client {1}", msg, client);
                    clientState.PendingToSend.Enqueue(msg);
                    return;
                }

                // if the queue is non empty - drain it first.
                if (clientState.PendingToSend.Count > 0)
                {
                    if (msg != null)
                        clientState.PendingToSend.Enqueue(msg);
                    
                    // For now, drain in-line, although in the future this should happen in yet another asynch agent
                    Drain(clientState);
                    return;
                }
                // the queue was empty AND we are connected.

                // If the request includes a message to send, send it (or enqueue it for later)
                if (msg == null) return;

                if (!Send(msg, clientState.Socket))
                {
                    if (Log.IsVerbose3) Log.Verbose3("Queued message {0} for client {1}", msg, client);
                    clientState.PendingToSend.Enqueue(msg);
                }
                else
                {
                    if (Log.IsVerbose3) Log.Verbose3("Sent message {0} to client {1}", msg, client);
                }
            }

            protected override void ProcessBatch(List<OutgoingClientMessage> requests)
            {
                if (Cts.IsCancellationRequested) return;
                
                if (requests == null || requests.Count == 0) return;

                // Every Tuple in requests are guaranteed to have the same client
                var client = requests[0].Item1;
                var msgs = requests.Where(r => r != null).Select(r => r.Item2).ToList();

                // Find the client state
                ClientState clientState;
                bool found;
                lock (gateway.lockable)
                {
                    found = gateway.clients.TryGetValue(client, out clientState);
                }

                // This should never happen -- but make sure to handle it reasonably, just in case
                if (!found || (clientState == null))
                {
                    if (msgs.Count == 0) return;

                    Log.Info(ErrorCode.GatewayTryingToSendToUnrecognizedClient, "Trying to send {0} messages to an unrecognized client {1}. First msg {0}",
                        msgs.Count, client, msgs[0].ToString());

                    foreach (var msg in msgs)
                    {
                        MessagingStatisticsGroup.OnFailedSentMessage(msg);
                        // Message for unrecognized client -- reject it
                        if (msg.Direction == Message.Directions.Request)
                        {
                            MessagingStatisticsGroup.OnRejectedMessage(msg);
                            Message error = msg.CreateRejectionResponse(Message.RejectionTypes.Unrecoverable, "Unknown client " + client);
                            gateway.SendMessage(error);
                        }
                        else
                        {
                            MessagingStatisticsGroup.OnDroppedSentMessage(msg);
                        }
                    }
                    return;
                }

                // if disconnected - queue for later.
                if (!clientState.IsConnected)
                {
                    if (msgs.Count == 0) return;

                    if (Log.IsVerbose3) Log.Verbose3("Queued {0} messages for client {1}", msgs.Count, client);
                    clientState.PendingBatchesToSend.Enqueue(msgs);
                    return;
                }

                // if the queue is non empty - drain it first.
                if (clientState.PendingBatchesToSend.Count > 0)
                {
                    if (msgs.Count != 0)
                        clientState.PendingBatchesToSend.Enqueue(msgs);
                    
                    // For now, drain in-line, although in the future this should happen in yet another asynch agent
                    DrainBatch(clientState);
                    return;
                }
                // the queue was empty AND we are connected.

                // If the request includes a message to send, send it (or enqueue it for later)
                if (msgs.Count == 0) return;

                if (!SendBatch(msgs, clientState.Socket))
                {
                    if (Log.IsVerbose3) Log.Verbose3("Queued {0} messages for client {1}", msgs.Count, client);
                    clientState.PendingBatchesToSend.Enqueue(msgs);
                }
                else
                {
                    if (Log.IsVerbose3) Log.Verbose3("Sent {0} message to client {1}", msgs.Count, client);
                }
            }

            private void Drain(ClientState clientState)
            {
                // For now, drain in-line, although in the future this should happen in yet another asynch agent
                while (clientState.PendingToSend.Count > 0)
                {
                    var m = clientState.PendingToSend.Peek();
                    if (Send(m, clientState.Socket))
                    {
                        if (Log.IsVerbose3) Log.Verbose3("Sent queued message {0} to client {1}", m, clientState.Id);
                        clientState.PendingToSend.Dequeue();
                    }
                    else
                    {
                        return;
                    }
                }
            }

            private void DrainBatch(ClientState clientState)
            {
                // For now, drain in-line, although in the future this should happen in yet another asynch agent
                while (clientState.PendingBatchesToSend.Count > 0)
                {
                    var m = clientState.PendingBatchesToSend.Peek();
                    if (SendBatch(m, clientState.Socket))
                    {
                        if (Log.IsVerbose3) Log.Verbose3("Sent {0} queued messages to client {1}", m.Count, clientState.Id);
                        clientState.PendingBatchesToSend.Dequeue();
                    }
                    else
                    {
                        return;
                    }
                }
            }


            private bool Send(Message msg, Socket sock)
            {
                if (Cts.IsCancellationRequested) return false;
                
                if (sock == null) return false;
                
                // Send the message
                List<ArraySegment<byte>> data;
                int headerLength;
                try
                {
                    data = msg.Serialize(out headerLength);
                }
                catch (Exception exc)
                {
                    OnMessageSerializationFailure(msg, exc);
                    return true;
                }

                int length = data.Sum(x => x.Count);

                int bytesSent = 0;
                bool exceptionSending = false;
                bool countMismatchSending = false;
                string sendErrorStr;
                try
                {
                    bytesSent = sock.Send(data);
                    if (bytesSent != length)
                    {
                        // The complete message wasn't sent, even though no error was reported; treat this as an error
                        countMismatchSending = true;
                        sendErrorStr = String.Format("Byte count mismatch on send: sent {0}, expected {1}", bytesSent, length);
                        Log.Warn(ErrorCode.GatewayByteCountMismatch, sendErrorStr);
                    }
                }
                catch (Exception exc)
                {
                    exceptionSending = true;
                    string remoteEndpoint = "";
                    if (!(exc is ObjectDisposedException))
                    {
                        remoteEndpoint = sock.RemoteEndPoint.ToString();
                    }
                    sendErrorStr = String.Format("Exception sending to client at {0}: {1}", remoteEndpoint, exc);
                    Log.Warn(ErrorCode.GatewayExceptionSendingToClient, sendErrorStr, exc);
                }
                MessagingStatisticsGroup.OnMessageSend(msg.TargetSilo, msg.Direction, bytesSent, headerLength, SocketDirection.GatewayToClient);
                bool sendError = exceptionSending || countMismatchSending;
                if (sendError)
                {
                    gateway.RecordClosedSocket(sock);
                    SocketManager.CloseSocket(sock);
                }
                gatewaySends.Increment();
                msg.ReleaseBodyAndHeaderBuffers();
                return !sendError;
            }

            private bool SendBatch(List<Message> msgs, Socket sock)
            {
                if (Cts.IsCancellationRequested) return false;
                if (sock == null) return false;
                if (msgs == null || msgs.Count == 0) return true;
                
                // Send the message
                List<ArraySegment<byte>> data;
                int headerLengths;
                bool continueSend = OutgoingMessageSender.SerializeMessages(msgs, out data, out headerLengths, OnMessageSerializationFailure);
                if (!continueSend) return false;

                int length = data.Sum(x => x.Count);

                int bytesSent = 0;
                bool exceptionSending = false;
                bool countMismatchSending = false;
                string sendErrorStr;

                try
                {
                    bytesSent = sock.Send(data);
                    if (bytesSent != length)
                    {
                        // The complete message wasn't sent, even though no error was reported; treat this as an error
                        countMismatchSending = true;
                        sendErrorStr = String.Format("Byte count mismatch on send: sent {0}, expected {1}", bytesSent, length);
                        Log.Warn(ErrorCode.GatewayByteCountMismatch, sendErrorStr);
                    }
                }
                catch (Exception exc)
                {
                    exceptionSending = true;
                    string remoteEndpoint = "";
                    if (!(exc is ObjectDisposedException))
                    {
                        remoteEndpoint = sock.RemoteEndPoint.ToString();
                    }
                    sendErrorStr = String.Format("Exception sending to client at {0}: {1}", remoteEndpoint, exc);
                    Log.Warn(ErrorCode.GatewayExceptionSendingToClient, sendErrorStr, exc);
                }

                MessagingStatisticsGroup.OnMessageBatchSend(msgs[0].TargetSilo, msgs[0].Direction, bytesSent, headerLengths, SocketDirection.GatewayToClient, msgs.Count);
                bool sendError = exceptionSending || countMismatchSending;
                if (sendError)
                {
                    gateway.RecordClosedSocket(sock);
                    SocketManager.CloseSocket(sock);
                }
                gatewaySends.Increment();
                foreach (Message msg in msgs)
                    msg.ReleaseBodyAndHeaderBuffers();
                
                return !sendError;
            }

            private void OnMessageSerializationFailure(Message msg, Exception exc)
            {
                // we only get here if we failed to serialize the msg (or any other catastrophic failure).
                // Request msg fails to serialize on the sending silo, so we just enqueue a rejection msg.
                // Response msg fails to serialize on the responding silo, so we try to send an error response back.
                Log.Warn(ErrorCode.Messaging_Gateway_SerializationError, String.Format("Unexpected error serializing message {0} on the gateway", msg.ToString()), exc);
                msg.ReleaseBodyAndHeaderBuffers();
                MessagingStatisticsGroup.OnFailedSentMessage(msg);
                MessagingStatisticsGroup.OnDroppedSentMessage(msg);
            }
        }
    }
}