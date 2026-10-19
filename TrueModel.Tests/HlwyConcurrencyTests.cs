using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TrueModel;

namespace TrueModel.Tests;

public class HlwyConcurrencyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    public async Task UsesBoundedWorkersAndKeepsEverySample(int concurrency)
    {
        var handler = new Handler(Math.Min(40, concurrency));
        using var http = new HttpClient(handler);
        var task = new ModelTraceClient(http).TestHlwy("https://mock.test", "secret", "mock",
            new AppSettings { HlwySampleCount = 40, HlwyConcurrency = concurrency }, CancellationToken.None, includeSupplemental: false);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Math.Min(40, concurrency), handler.Peak);
        }
        finally { handler.Release.TrySetResult(); }
        var result = await task;
        Assert.Equal(40, handler.Count);
        Assert.Equal(40, result.GetProperty("result").GetProperty("UsedOutputs").GetInt32());
        Assert.Equal(Enumerable.Range(1, 40).Select(i => $"hlwy-{i}"),
            result.GetProperty("responses").EnumerateArray().Select(r => r.GetProperty("Id").GetString()));
        Assert.False(result.TryGetProperty("candy", out _));
        Assert.InRange(handler.Peak, 1, concurrency);
    }

    [Fact]
    public async Task CancellingStopsAllInFlightWorkersWithoutDispatchingMore()
    {
        var handler = new Handler(5);
        using var http = new HttpClient(handler);
        using var cancel = new CancellationTokenSource();
        var task = new ModelTraceClient(http).TestHlwy("https://mock.test", "secret", "mock", new AppSettings(), cancel.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(5, handler.Count);
        Assert.Equal(0, handler.Active);
    }

    [Fact]
    public async Task ConsecutiveErrorsStopDispatchButDrainInFlightRequests()
    {
        var handler = new Handler(5) { Text = "invalid" };
        using var http = new HttpClient(handler);
        var task = new ModelTraceClient(http).TestHlwy("https://mock.test", "secret", "mock", new AppSettings(), CancellationToken.None, false);
        try { await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { handler.Release.TrySetResult(); }
        var result = await task;
        Assert.InRange(handler.Count, 5, 9);
        Assert.Equal(handler.Count, result.GetProperty("responses").GetArrayLength());
        Assert.True(result.TryGetProperty("error", out _));
        Assert.Equal(0, handler.Active);
    }

    private sealed class Handler(int firstWave) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active, Count, Peak;
        public string Text { get; init; } = "42";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Count);
            var active = Interlocked.Increment(ref Active);
            lock (this) Peak = Math.Max(Peak, active);
            if (active == firstWave) Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(token);
                await Task.Delay(5, token);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { message = new { content = Text }, finish_reason = "stop" } } }) };
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
