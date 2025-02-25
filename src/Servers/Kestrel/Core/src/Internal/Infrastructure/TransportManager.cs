// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Server.Kestrel.Https.Internal;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;

internal sealed class TransportManager
{
    private readonly List<ActiveTransport> _transports = new List<ActiveTransport>();

    private readonly List<IConnectionListenerFactory> _transportFactories;
    private readonly List<IMultiplexedConnectionListenerFactory> _multiplexedTransportFactories;
    private readonly ServiceContext _serviceContext;

    public TransportManager(
        List<IConnectionListenerFactory> transportFactories,
        List<IMultiplexedConnectionListenerFactory> multiplexedTransportFactories,
        ServiceContext serviceContext)
    {
        _transportFactories = transportFactories;
        _multiplexedTransportFactories = multiplexedTransportFactories;
        _serviceContext = serviceContext;
    }

    private ConnectionManager ConnectionManager => _serviceContext.ConnectionManager;
    private KestrelTrace Trace => _serviceContext.Log;

    public async Task<EndPoint> BindAsync(EndPoint endPoint, ConnectionDelegate connectionDelegate, ListenOptions listenOptions, CancellationToken cancellationToken)
    {
        if (_transportFactories.Count == 0)
        {
            throw new InvalidOperationException($"Cannot bind with {nameof(ConnectionDelegate)} no {nameof(IConnectionListenerFactory)} is registered.");
        }

        foreach (var transportFactory in _transportFactories)
        {
            var selector = transportFactory as IConnectionListenerFactorySelector;
            if (CanBindFactory(endPoint, selector))
            {
                var transport = await transportFactory.BindAsync(endPoint, cancellationToken).ConfigureAwait(false);
                StartAcceptLoop(new GenericConnectionListener(transport), c => connectionDelegate(c), listenOptions);
                return transport.EndPoint;
            }
        }

        throw new InvalidOperationException($"No registered {nameof(IConnectionListenerFactory)} supports endpoint {endPoint.GetType().Name}: {endPoint}");
    }

    public async Task<EndPoint> BindAsync(EndPoint endPoint, MultiplexedConnectionDelegate multiplexedConnectionDelegate, ListenOptions listenOptions, CancellationToken cancellationToken)
    {
        if (_multiplexedTransportFactories.Count == 0)
        {
            throw new InvalidOperationException($"Cannot bind with {nameof(MultiplexedConnectionDelegate)} no {nameof(IMultiplexedConnectionListenerFactory)} is registered.");
        }

        var features = new FeatureCollection();

        // HttpsOptions or HttpsCallbackOptions should always be set in production, but it's not set for InMemory tests.
        // The QUIC transport will check if TlsConnectionCallbackOptions is missing.
        if (listenOptions.HttpsOptions != null)
        {
            var sslServerAuthenticationOptions = HttpsConnectionMiddleware.CreateHttp3Options(listenOptions.HttpsOptions);
            features.Set(new TlsConnectionCallbackOptions
            {
                ApplicationProtocols = sslServerAuthenticationOptions.ApplicationProtocols ?? new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
                OnConnection = (context, cancellationToken) => ValueTask.FromResult(sslServerAuthenticationOptions),
                OnConnectionState = null,
            });
        }
        else if (listenOptions.HttpsCallbackOptions != null)
        {
            features.Set(new TlsConnectionCallbackOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
                OnConnection = (context, cancellationToken) =>
                {
                    return listenOptions.HttpsCallbackOptions.OnConnection(new TlsHandshakeCallbackContext
                    {
                        ClientHelloInfo = context.ClientHelloInfo,
                        CancellationToken = cancellationToken,
                        State = context.State,
                        Connection = new ConnectionContextAdapter(context.Connection),
                    });
                },
                OnConnectionState = listenOptions.HttpsCallbackOptions.OnConnectionState,
            });
        }

        foreach (var multiplexedTransportFactory in _multiplexedTransportFactories)
        {
            var selector = multiplexedTransportFactory as IConnectionListenerFactorySelector;
            if (CanBindFactory(endPoint, selector))
            {
                var transport = await multiplexedTransportFactory.BindAsync(endPoint, features, cancellationToken).ConfigureAwait(false);
                StartAcceptLoop(new GenericMultiplexedConnectionListener(transport), c => multiplexedConnectionDelegate(c), listenOptions);
                return transport.EndPoint;
            }
        }

        throw new InvalidOperationException($"No registered {nameof(IMultiplexedConnectionListenerFactory)} supports endpoint {endPoint.GetType().Name}: {endPoint}");
    }

    private static bool CanBindFactory(EndPoint endPoint, IConnectionListenerFactorySelector? selector)
    {
        // By default, the last registered factory binds to the endpoint.
        // A factory can implement IConnectionListenerFactorySelector to decide whether it can bind to the endpoint.
        return selector?.CanBind(endPoint) ?? true;
    }

    /// <summary>
    /// TlsHandshakeCallbackContext.Connection is ConnectionContext but QUIC connection only implements BaseConnectionContext.
    /// </summary>
    private sealed class ConnectionContextAdapter : ConnectionContext
    {
        private readonly BaseConnectionContext _inner;

        public ConnectionContextAdapter(BaseConnectionContext inner) => _inner = inner;

        public override IDuplexPipe Transport
        {
            get => throw new NotSupportedException("Not supported by HTTP/3 connections.");
            set => throw new NotSupportedException("Not supported by HTTP/3 connections.");
        }
        public override string ConnectionId
        {
            get => _inner.ConnectionId;
            set => _inner.ConnectionId = value;
        }
        public override IFeatureCollection Features => _inner.Features;
        public override IDictionary<object, object?> Items
        {
            get => _inner.Items;
            set => _inner.Items = value;
        }
        public override EndPoint? LocalEndPoint
        {
            get => _inner.LocalEndPoint;
            set => _inner.LocalEndPoint = value;
        }
        public override EndPoint? RemoteEndPoint
        {
            get => _inner.RemoteEndPoint;
            set => _inner.RemoteEndPoint = value;
        }
        public override CancellationToken ConnectionClosed
        {
            get => _inner.ConnectionClosed;
            set => _inner.ConnectionClosed = value;
        }
        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private void StartAcceptLoop<T>(IConnectionListener<T> connectionListener, Func<T, Task> connectionDelegate, ListenOptions listenOptions) where T : BaseConnectionContext
    {
        var transportConnectionManager = new TransportConnectionManager(_serviceContext.ConnectionManager);
        var connectionDispatcher = new ConnectionDispatcher<T>(_serviceContext, connectionDelegate, transportConnectionManager, listenOptions);
        var acceptLoopTask = connectionDispatcher.StartAcceptingConnections(connectionListener);

        _transports.Add(new ActiveTransport(connectionListener, acceptLoopTask, transportConnectionManager, listenOptions.EndpointConfig));
    }

    public Task StopEndpointsAsync(List<EndpointConfig> endpointsToStop, CancellationToken cancellationToken)
    {
        var transportsToStop = _transports.Where(t => t.EndpointConfig != null && endpointsToStop.Contains(t.EndpointConfig)).ToList();
        return StopTransportsAsync(transportsToStop, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return StopTransportsAsync(new List<ActiveTransport>(_transports), cancellationToken);
    }

    private async Task StopTransportsAsync(List<ActiveTransport> transportsToStop, CancellationToken cancellationToken)
    {
        var tasks = new Task[transportsToStop.Count];

        for (int i = 0; i < transportsToStop.Count; i++)
        {
            tasks[i] = transportsToStop[i].UnbindAsync(cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        async Task StopTransportConnection(ActiveTransport transport)
        {
            if (!await transport.TransportConnectionManager.CloseAllConnectionsAsync(cancellationToken).ConfigureAwait(false))
            {
                Trace.NotAllConnectionsClosedGracefully();

                if (!await transport.TransportConnectionManager.AbortAllConnectionsAsync().ConfigureAwait(false))
                {
                    Trace.NotAllConnectionsAborted();
                }
            }
        }

        for (int i = 0; i < transportsToStop.Count; i++)
        {
            tasks[i] = StopTransportConnection(transportsToStop[i]);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        for (int i = 0; i < transportsToStop.Count; i++)
        {
            tasks[i] = transportsToStop[i].DisposeAsync().AsTask();
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var transport in transportsToStop)
        {
            _transports.Remove(transport);
        }
    }

    private sealed class ActiveTransport : IAsyncDisposable
    {
        public ActiveTransport(IConnectionListenerBase transport, Task acceptLoopTask, TransportConnectionManager transportConnectionManager, EndpointConfig? endpointConfig = null)
        {
            ConnectionListener = transport;
            AcceptLoopTask = acceptLoopTask;
            TransportConnectionManager = transportConnectionManager;
            EndpointConfig = endpointConfig;
        }

        public IConnectionListenerBase ConnectionListener { get; }
        public Task AcceptLoopTask { get; }
        public TransportConnectionManager TransportConnectionManager { get; }

        public EndpointConfig? EndpointConfig { get; }

        public async Task UnbindAsync(CancellationToken cancellationToken)
        {
            await ConnectionListener.UnbindAsync(cancellationToken).ConfigureAwait(false);
            await AcceptLoopTask.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            return ConnectionListener.DisposeAsync();
        }
    }

    private sealed class GenericConnectionListener : IConnectionListener<ConnectionContext>
    {
        private readonly IConnectionListener _connectionListener;
        private readonly IConcurrentConnectionListener? _concurrentListener;

        public GenericConnectionListener(IConnectionListener connectionListener)
        {
            _connectionListener = connectionListener;
            _concurrentListener = connectionListener as IConcurrentConnectionListener;
        }

        public EndPoint EndPoint => _connectionListener.EndPoint;

        public ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
             => _connectionListener.AcceptAsync(cancellationToken);

        public int MaxAccepts => _concurrentListener?.MaxAccepts ?? 1;

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
            => _connectionListener.UnbindAsync(cancellationToken);

        public ValueTask DisposeAsync()
            => _connectionListener.DisposeAsync();

        public IAsyncEnumerable<object> AcceptManyAsync()
            => _concurrentListener is null ? FallbackAcceptManyAsync(CancellationToken.None) : _concurrentListener.AcceptManyAsync();

        public ConnectionContext Accept(object token)
            => _concurrentListener is null ? (ConnectionContext)token : _concurrentListener.Accept(token);

        private async IAsyncEnumerable<object> FallbackAcceptManyAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                var connection = await _connectionListener.AcceptAsync(cancellationToken);
                if (connection is null)
                {
                    break;
                }
                yield return connection;
            }
        }
    }

    private sealed class GenericMultiplexedConnectionListener : IConnectionListener<MultiplexedConnectionContext>
    {
        private readonly IMultiplexedConnectionListener _multiplexedConnectionListener;

        public GenericMultiplexedConnectionListener(IMultiplexedConnectionListener multiplexedConnectionListener)
        {
            _multiplexedConnectionListener = multiplexedConnectionListener;
        }

        public EndPoint EndPoint => _multiplexedConnectionListener.EndPoint;

        public int MaxAccepts => 1;

        public ValueTask<MultiplexedConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
             => _multiplexedConnectionListener.AcceptAsync(features: null, cancellationToken);

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
            => _multiplexedConnectionListener.UnbindAsync(cancellationToken);

        public ValueTask DisposeAsync()
            => _multiplexedConnectionListener.DisposeAsync();

        public IAsyncEnumerable<object> AcceptManyAsync() => FallbackAcceptManyAsync(CancellationToken.None);

        private async IAsyncEnumerable<object> FallbackAcceptManyAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                var connection = await _multiplexedConnectionListener.AcceptAsync(features: null, cancellationToken);
                if (connection is null)
                {
                    break;
                }
                yield return connection;
            }
        }

        public MultiplexedConnectionContext Accept(object token) => (MultiplexedConnectionContext)token;
    }
}
