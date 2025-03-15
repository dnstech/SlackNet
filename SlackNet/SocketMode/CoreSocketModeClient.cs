﻿#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SlackNet.SocketMode;

public interface ICoreSocketModeClient : IDisposable
{
    Task Connect(SocketModeConnectionOptions? connectionOptions = null, CancellationToken cancellationToken = default);
    void Disconnect();

    /// <summary>
    /// Is the client connecting or has it connected.
    /// </summary>
    bool Connected { get; }

    IObservable<RawSocketMessage> RawSocketMessages { get; }

    IObservable<SocketMessage> Messages { get; }

    /// <summary>
    /// Sends an acknowledgement response, with an optional payload, back to Slack.
    /// </summary>
    /// <param name="socketId">
    ///     The ID of the web socket to send the acknowledgement on.
    ///     Must be the same as the web socket that received the message being acknowledged.
    /// </param>
    /// <param name="acknowledgement">
    ///     The response to send to Slack.
    ///     Should contain the <see cref="SocketEnvelope.EnvelopeId"/> of the message being responded to.
    /// </param>
    Task Send(int socketId, Acknowledgement acknowledgement);
}

public class CoreSocketModeClient : ICoreSocketModeClient
{
    private readonly ISlackApiClient _client;
    private readonly IWebSocketFactory _webSocketFactory;
    private readonly SlackJsonSettings _jsonSettings;
    private readonly IScheduler _scheduler;
    private readonly ILogger _log;
    private readonly Subject<RawSocketMessage> _rawSocketMessagesSubject = new();
    private readonly ISubject<RawSocketMessage> _rawSocketMessages;
    private List<ReconnectingWebSocket> _webSockets = new();
    private IDisposable? _rawSocketStringsSubscription;
    private CancellationTokenSource? _disconnectCancellation;
    private CancellationTokenSource? _connectionCancelled;

    public CoreSocketModeClient(string appLevelToken)
        : this(
            new SlackApiClient(appLevelToken),
            Default.WebSocketFactory,
            Default.JsonSettings(),
            Default.Scheduler,
            Default.Logger
        ) { }

    public CoreSocketModeClient(
        ISlackApiClient client,
        IWebSocketFactory webSocketFactory,
        SlackJsonSettings jsonSettings,
        IScheduler scheduler,
        ILogger logger)
    {
        _client = client;
        _webSocketFactory = webSocketFactory;
        _jsonSettings = jsonSettings;
        _scheduler = scheduler;
        _log = logger.ForSource<CoreSocketModeClient>();

        _rawSocketMessages = Subject.Synchronize(_rawSocketMessagesSubject);

        Messages = _rawSocketMessages
            .Select(DeserializeMessage)
            .WhereNotNull()
            .Publish()
            .RefCount();

        Messages
            .OfType<Disconnect>()
            .Do(m => _log.Internal("Socket {SocketId} disconnecting because {Reason}", m.SocketId, m.Reason))
            .Where(d => d.Reason == DisconnectReason.SocketModeDisabled)
            .Subscribe(_ => Disconnect());
    }

    private SocketMessage? DeserializeMessage(RawSocketMessage rawMessage)
    {
        try
        {
            var message = JsonConvert.DeserializeObject<SocketMessage>(rawMessage.Message, _jsonSettings.SerializerSettings)!;
            message.SocketId = rawMessage.SocketId;
            message.RequestId = rawMessage.RequestId;
            return message;
        }
        catch (Exception e)
        {
            _log.WithContext("SocketId", rawMessage.SocketId)
                .WithContext("RequestId", rawMessage.RequestId)
                .WithContext("Message", rawMessage.Message)
                .Error(e, "Error deserializing socket mode message");
            return null;
        }
    }

    public async Task Connect(SocketModeConnectionOptions? connectionOptions = null, CancellationToken cancellationToken = default)
    {
        if (Connected)
            throw new InvalidOperationException("Already connecting or connected");

        connectionOptions ??= Default.SocketModeConnectionOptions;

        _log.Internal("Opening {NumberOfConnections} socket mode connections, with delay of {ConnectionDelay}", connectionOptions.NumberOfConnections, connectionOptions.ConnectionDelay);

        _rawSocketStringsSubscription?.Dispose();
        Disconnect();

        _disconnectCancellation = new CancellationTokenSource();
        _connectionCancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disconnectCancellation.Token);

        _webSockets = Enumerable.Range(0, connectionOptions.NumberOfConnections)
            .Select(i => new ReconnectingWebSocket(_webSocketFactory, _scheduler, _log, i))
            .ToList();

        _rawSocketStringsSubscription = _webSockets
            .Select(ws => ws.Messages)
            .Merge()
            .Subscribe(_rawSocketMessages);

        var firstConnection = _webSockets.First().Connect(GetWebSocketUrl, _connectionCancelled.Token);

        // Stagger remaining connections so they don't all expire at the same time
        _webSockets.Skip(1).ToObservable()
            .Zip(Observable.Interval(connectionOptions.ConnectionDelay, _scheduler).Take(_webSockets.Count - 1), (ws, _) => ws)
            .Select(ws => ws.Connect(GetWebSocketUrl, _connectionCancelled.Token))
            .Subscribe();

        await firstConnection.ConfigureAwait(false);

        async Task<string> GetWebSocketUrl()
        {
            var openResponse = await _client.AppsConnectionsApi.Open(_connectionCancelled.Token).ConfigureAwait(false);
            return connectionOptions.DebugReconnects
                ? openResponse.Url + "&debug_reconnects=true"
                : openResponse.Url;
        }
    }

    public void Disconnect()
    {
        if (_disconnectCancellation is not null)
        {
            _log.Internal("Disconnecting previous socket mode connections");
            _disconnectCancellation?.Cancel();
            foreach (var webSocket in _webSockets)
                webSocket.Dispose();
        }
    }

    /// <summary>
    /// Is the client connecting or has it connected.
    /// </summary>
    public bool Connected =>
        _webSockets.Any(ws => ws.State is WebSocketState.Connecting or WebSocketState.Open);

    public IObservable<RawSocketMessage> RawSocketMessages => _rawSocketMessages.AsObservable();

    public IObservable<SocketMessage> Messages { get; }

    /// <summary>
    /// Sends an acknowledgement response, with an optional payload, back to Slack.
    /// </summary>
    /// <param name="socketId">
    ///     The ID of the web socket to send the acknowledgement on.
    ///     Must be the same as the web socket that received the message being acknowledged.
    /// </param>
    /// <param name="acknowledgement">
    ///     The response to send to Slack.
    ///     Should contain the <see cref="SocketEnvelope.EnvelopeId"/> of the message being responded to.
    /// </param>
    public async Task Send(int socketId, Acknowledgement acknowledgement)
    {
        if (_webSockets.ElementAtOrDefault(socketId) is ReconnectingWebSocket socket)
            await socket.Send(JsonConvert.SerializeObject(acknowledgement, _jsonSettings.SerializerSettings)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _connectionCancelled?.Dispose();
        _disconnectCancellation?.Dispose();
        _rawSocketStringsSubscription?.Dispose();
        _rawSocketMessagesSubject.Dispose();
        foreach (var webSocket in _webSockets)
            webSocket.Dispose();
    }
}