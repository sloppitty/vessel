using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Vessel.Tests;

public partial class HyperliquidAdapterTestsSupport
{
    // Synthetic fixtures only. All HttpClients below use an intercepting handler.
    internal const string Address = "0x0123456789abcdef0123456789ABCDEF01234567";
    internal const long StateTime = 1790812800000; // October 1, 2026 UTC
    internal const string Meta = """
        {"universe":[
          {"name":"BTC","szDecimals":5,"maxLeverage":40},
          {"name":"ETH","szDecimals":4,"maxLeverage":20,"isDelisted":true}
        ]}
        """;
    internal const string State = """
        {
          "marginSummary":{"accountValue":"12345.678901234567890123","totalMarginUsed":"250.123456789"},
          "crossMarginSummary":{"accountValue":"99999","totalMarginUsed":"999"},
          "withdrawable":"12095.555444445567890123","time":1790812800000,
          "assetPositions":[
            {"type":"oneWay","position":{
              "coin":"BTC","szi":"-0.01234567890123456789","entryPx":"61234.1234567890123456",
              "unrealizedPnl":"-12.987654321","marginUsed":"250.123456789",
              "leverage":{"type":"isolated","value":5,"rawUsd":"-5.1"}
            }},
            {"type":"oneWay","position":{"coin":"ETH","szi":"0","entryPx":null}},
            {"type":"oneWay","position":{"coin":"@123"}},
            {"type":"oneWay","position":{"coin":"PURR/USDC"}},
            {"type":"oneWay","position":{"coin":"xyz:BTC"}},
            {"type":"oneWay","position":{"coin":"UNKNOWN"}}
          ]
        }
        """;
    internal const string Fill = """
        {
          "coin":"BTC","px":"61235.1234567890123456","sz":"0.001234567890123456789",
          "side":"B","dir":"Close Short","time":1790812799000,"startPosition":"-0.02",
          "closedPnl":"1.234567890123456789","fee":"-0.000123456789","feeToken":"USDC",
          "oid":9007199254740993,"tid":18446744073709551615,
          "hash":"0xsynthetic-transaction","crossed":false,"builderFee":"0.00001"
        }
        """;

    internal static string Mutate(string json, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(json)!;
        change(node);
        return node.ToJsonString();
    }

    internal static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.hyperliquid.xyz/"), Timeout = TimeSpan.FromSeconds(30) };

    internal static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal static FixtureHandler Fixtures(params string[] fixtures) =>
        new((index, _) => Task.FromResult(Json(
            fixtures.Length == 3 && index >= 3 ? DefaultResponses()[index] : fixtures[index])));

    internal static string[] DefaultResponses() => [Meta, State, $"[{Fill}]", "\"default\"", SpotMeta, "{\"balances\":[]}"];

    internal sealed class FixtureHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<(HttpMethod Method, string Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, body));
            return await respond(Requests.Count - 1, cancellationToken);
        }
    }

    internal sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(StateTime);
        private readonly List<TestTimer> timers = [];
        internal int TimerCount { get; private set; }
        internal TimeSpan Deadline { get; private set; }
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            TimerCount++;
            Deadline = dueTime;
            var timer = new TestTimer(now + dueTime, callback, state);
            timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan interval)
        {
            now += interval;
            foreach (var timer in timers.ToArray()) timer.FireIfDue(now);
        }

        private sealed class TestTimer(DateTimeOffset due, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal void FireIfDue(DateTimeOffset now)
            {
                if (!disposed && now >= due)
                {
                    disposed = true;
                    callback(state);
                }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
