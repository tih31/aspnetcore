// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Tests;

public class ConnectionDispatcherTests : LoggedTest
{
    [Fact]
    public async Task OnConnectionCreatesLogScopeWithConnectionId()
    {
        var testLogger = new TestApplicationErrorLogger();
        var loggerFactory = new LoggerFactory(new[] { new KestrelTestLoggerProvider(testLogger) });

        var serviceContext = new TestServiceContext(loggerFactory);
        // This needs to run inline
        var tcs = new TaskCompletionSource();

        var connection = new Mock<DefaultConnectionContext> { CallBase = true }.Object;
        connection.ConnectionClosed = new CancellationToken(canceled: true);
        var transportConnectionManager = new TransportConnectionManager(serviceContext.ConnectionManager);
        var kestrelConnection = new KestrelConnection<ConnectionContext>(0, serviceContext, transportConnectionManager, _ => tcs.Task, connection, serviceContext.Log);
        transportConnectionManager.AddConnection(0, kestrelConnection);

        var task = kestrelConnection.ExecuteAsync();

        // The scope should be created
        var scopeObjects = testLogger.Scopes.OfType<IReadOnlyList<KeyValuePair<string, object>>>().ToList();

        Assert.Single(scopeObjects);
        var pairs = scopeObjects[0].ToDictionary(p => p.Key, p => p.Value);
        Assert.True(pairs.ContainsKey("ConnectionId"));
        Assert.Equal(connection.ConnectionId, pairs["ConnectionId"]);

        tcs.TrySetResult();

        await task;

        // Verify the scope was disposed after request processing completed
        Assert.True(testLogger.Scopes.IsEmpty);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task StartAcceptingConnectionsAsyncLogsIfAcceptAsyncThrows(int concurrency)
    {
        var serviceContext = new TestServiceContext(LoggerFactory);

        var dispatcher = new ConnectionDispatcher<ConnectionContext>(serviceContext, _ => Task.CompletedTask, new TransportConnectionManager(serviceContext.ConnectionManager),
            new(new IPEndPoint(IPAddress.Any, 80)));

        await dispatcher.StartAcceptingConnections(new ThrowingListener(concurrency));

        var criticalList = TestSink.Writes.Where(m => m.LogLevel == LogLevel.Critical).ToList();
        Assert.Equal(concurrency, criticalList.Count);
        var critical = criticalList.FirstOrDefault();
        Assert.NotNull(critical);
        Assert.IsType<InvalidOperationException>(critical.Exception);
        Assert.Equal("Unexpected error listening", critical.Exception.Message);
    }

    [Fact]
    public async Task OnConnectionFiresOnCompleted()
    {
        var serviceContext = new TestServiceContext();

        var connection = new Mock<DefaultConnectionContext> { CallBase = true }.Object;
        connection.ConnectionClosed = new CancellationToken(canceled: true);
        var transportConnectionManager = new TransportConnectionManager(serviceContext.ConnectionManager);
        var kestrelConnection = new KestrelConnection<ConnectionContext>(0, serviceContext, transportConnectionManager, _ => Task.CompletedTask, connection, serviceContext.Log);
        transportConnectionManager.AddConnection(0, kestrelConnection);
        var completeFeature = kestrelConnection.TransportConnection.Features.Get<IConnectionCompleteFeature>();

        Assert.NotNull(completeFeature);
        object stateObject = new object();
        object callbackState = null;
        completeFeature.OnCompleted(state => { callbackState = state; return Task.CompletedTask; }, stateObject);

        await kestrelConnection.ExecuteAsync();

        Assert.Equal(stateObject, callbackState);
    }

    [Fact]
    public async Task OnConnectionOnCompletedExceptionCaught()
    {
        var serviceContext = new TestServiceContext(LoggerFactory);
        var connection = new Mock<DefaultConnectionContext> { CallBase = true }.Object;
        connection.ConnectionClosed = new CancellationToken(canceled: true);
        var transportConnectionManager = new TransportConnectionManager(serviceContext.ConnectionManager);
        var kestrelConnection = new KestrelConnection<ConnectionContext>(0, serviceContext, transportConnectionManager, _ => Task.CompletedTask, connection, serviceContext.Log);
        transportConnectionManager.AddConnection(0, kestrelConnection);
        var completeFeature = kestrelConnection.TransportConnection.Features.Get<IConnectionCompleteFeature>();

        Assert.NotNull(completeFeature);
        object stateObject = new object();
        object callbackState = null;
        completeFeature.OnCompleted(state => { callbackState = state; throw new InvalidTimeZoneException(); }, stateObject);

        await kestrelConnection.ExecuteAsync();

        Assert.Equal(stateObject, callbackState);
        var errors = TestSink.Writes.Where(e => e.LogLevel >= LogLevel.Error).ToArray();
        Assert.Single(errors);
        Assert.Equal("An error occurred running an IConnectionCompleteFeature.OnCompleted callback.", errors[0].Message);
    }

    private class ThrowingListener : IConnectionListener<ConnectionContext>, IConcurrentConnectionListener
    {
        private readonly int _concurrency;
        public ThrowingListener(int concurrency) => _concurrency = concurrency;

        public EndPoint EndPoint { get; set; }

        public int MaxAccepts => _concurrency;

        public ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Unexpected error listening");
        }

        public ValueTask DisposeAsync()
        {
            return default;
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            return default;
        }
    }
}
