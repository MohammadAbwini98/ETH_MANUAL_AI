using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EthSignal.Domain.Models;
using EthSignal.Infrastructure.Apis;
using EthSignal.Infrastructure.Db;
using EthSignal.Infrastructure.Engine;
using EthSignal.Infrastructure.Engine.ML;
using EthSignal.Infrastructure.Trading;
using EthSignal.Web.BackgroundServices;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;

namespace EthSignal.Tests.Web;

/// <summary>P8 tests: API endpoint availability and response validation.</summary>
public class ApiEndpointTests : IClassFixture<ApiEndpointTests.TestApp>
{
    private readonly HttpClient _client;
    private readonly TestApp _factory;

    public ApiEndpointTests(TestApp factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>P8-T1: All required endpoints return valid JSON.</summary>
    [Theory]
    [InlineData("/health")]
    [InlineData("/api/quote/current")]
    [InlineData("/api/candles")]
    [InlineData("/api/indicators/current")]
    [InlineData("/api/indicators/history?timeframe=5m&limit=10")]
    [InlineData("/api/regime/current")]
    [InlineData("/api/signals/latest")]
    [InlineData("/api/signals/history?limit=10")]
    [InlineData("/api/blocked-signals/history?limit=10")]
    [InlineData("/api/generated-signals/history?limit=10")]
    [InlineData("/api/trading/queue?limit=10")]
    [InlineData("/api/performance/summary")]
    [InlineData("/api/performance/daily")]
    [InlineData("/api/admin/candle-sync/status")]
    [InlineData("/api/admin/ml/diagnostics")]
    public async Task Endpoint_Returns_Ok_Json(string path)
    {
        var response = await _client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var json = await response.Content.ReadAsStringAsync();
        var parsed = JsonDocument.Parse(json);
        parsed.Should().NotBeNull();
    }

    /// <summary>P8-T4: Latest signal endpoint returns all required fields.</summary>
    [Fact]
    public async Task Latest_Signal_Contains_Direction()
    {
        var response = await _client.GetAsync("/api/signals/latest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("direction", out _).Should().BeTrue(
            "signal response must include direction field");
    }

    [Fact]
    public async Task Latest_Signal_Prefers_Primary_Timeframe()
    {
        var response = await _client.GetAsync("/api/signals/latest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        // T2-6: Response now wraps signal in { signal, scalpSignal1m, resolvedTimeframe, direction }
        doc.RootElement.GetProperty("resolvedTimeframe").GetString().Should().Be("5m");
    }

    /// <summary>P8-T5: History endpoint returns paginated object with signals array.</summary>
    [Fact]
    public async Task Signal_History_Returns_Array()
    {
        var response = await _client.GetAsync("/api/signals/history?limit=5");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.TryGetProperty("total", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("page", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("pageSize", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Signal_History_Includes_Execution_State_When_Trade_Exists()
    {
        var response = await _client.GetAsync("/api/signals/history?limit=5");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var signals = doc.RootElement.GetProperty("signals");
        signals.GetArrayLength().Should().BeGreaterThan(0);
        signals[0].TryGetProperty("execution", out var execution).Should().BeTrue();
        execution.GetProperty("status").GetString().Should().Be("Open");
    }

    [Fact]
    public async Task Blocked_Signal_History_Returns_Stats_And_Array()
    {
        var response = await _client.GetAsync("/api/blocked-signals/history?limit=5");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("signals", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("stats", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("total", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Generated_Signal_History_Returns_Stats_And_Array()
    {
        var response = await _client.GetAsync("/api/generated-signals/history?limit=5");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("signals", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("stats", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("total", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Generated_Signal_History_Includes_Execution_State_When_Trade_Exists()
    {
        var response = await _client.GetAsync("/api/generated-signals/history?limit=5");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var signal = doc.RootElement.GetProperty("signals")[0];
        signal.GetProperty("execution").GetProperty("status").GetString().Should().Be("Loss");
    }

    [Fact]
    public async Task Executed_Trades_Returns_Array_And_Total()
    {
        var response = await _client.GetAsync("/api/executed-trades?limit=5&page=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("trades", out var trades).Should().BeTrue();
        trades.GetArrayLength().Should().Be(1);
        trades[0].GetProperty("accountName").GetString().Should().Be("DEMOAI");
        trades[0].GetProperty("isDemo").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Executed_Trades_Returns_Lifecycle_Status_Field()
    {
        var response = await _client.GetAsync("/api/executed-trades?limit=5&page=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var trade = doc.RootElement.GetProperty("trades")[0];
        trade.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();
        trade.TryGetProperty("closeSource", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Trading_Account_Summary_Returns_DemoAiDetails()
    {
        var response = await _client.GetAsync("/api/trading/account-summary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("accountName").GetString().Should().Be("DEMOAI");
        doc.RootElement.GetProperty("isDemo").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Trading_Health_Returns_Demo_Status()
    {
        var response = await _client.GetAsync("/api/trading/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("demoOnly").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("sessionReady").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("accountName").GetString().Should().Be("DEMOAI");
        doc.RootElement.GetProperty("activeAccountIsDemo").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Trading_Execution_Stats_Returns_Expected_Fields()
    {
        var response = await _client.GetAsync("/api/trading/execution-stats");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("totalExecuted").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("openTrades").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("currency").GetString().Should().Be("USD");
    }

    [Fact]
    public async Task Trading_Queue_Returns_Status_Ids_And_Times()
    {
        var response = await _client.GetAsync("/api/trading/queue?limit=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("activeTradeCount").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("brokerOpenTradeCount").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("pendingSubmissionCount").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("queueConcurrentRequestLimit").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("availableDispatchSlots").GetInt32().Should().Be(1);
        doc.RootElement.TryGetProperty("serverTimeUtc", out _).Should().BeTrue();
        var entries = doc.RootElement.GetProperty("entries");
        entries.GetArrayLength().Should().Be(1);
        entries[0].GetProperty("queueEntryId").GetInt64().Should().Be(71);
        entries[0].GetProperty("signalId").GetString().Should().Be("55555555-5555-5555-5555-555555555555");
        entries[0].TryGetProperty("createdAtUtc", out _).Should().BeTrue();
        entries[0].TryGetProperty("updatedAtUtc", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Executed_Trades_Reset_Returns_Targeted_Reset_Result()
    {
        var response = await _client.PostAsync("/api/executed-trades/reset", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("queueEntriesCleared").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("executedTradesCleared").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("executionAttemptsCleared").GetInt32().Should().Be(4);
        doc.RootElement.GetProperty("executionEventsCleared").GetInt32().Should().Be(5);
        doc.RootElement.GetProperty("accountSnapshotsCleared").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("closeActionsCleared").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Admin_Global_Config_Returns_Recommended_Executor_Flag()
    {
        using var client = _factory.WithWebHostBuilder(_ => { }).CreateClient();
        var response = await client.GetAsync("/api/admin/global-config");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("recommendedSignalExecutionEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Admin_Global_Config_Patch_Updates_Recommended_Executor_Flag()
    {
        using var client = _factory.WithWebHostBuilder(_ => { }).CreateClient();
        var response = await client.PatchAsJsonAsync("/api/admin/global-config", new
        {
            recommendedSignalExecutionEnabled = false
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var followUp = await client.GetAsync("/api/admin/global-config");
        followUp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await followUp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("recommendedSignalExecutionEnabled").GetBoolean().Should().BeFalse();
    }

    /// <summary>P8-T1: Performance summary contains expected stat fields.</summary>
    [Fact]
    public async Task Performance_Summary_Has_Stats()
    {
        var response = await _client.GetAsync("/api/performance/summary");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.TryGetProperty("winRate", out _).Should().BeTrue();
        root.TryGetProperty("totalSignals", out _).Should().BeTrue();
        root.TryGetProperty("profitFactor", out _).Should().BeTrue();
    }

    /// <summary>P8-T1: Health endpoint returns status.</summary>
    [Fact]
    public async Task Health_Returns_Status()
    {
        var response = await _client.GetAsync("/health");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("running");
    }

    [Fact]
    public async Task Candle_Sync_Status_Returns_Persisted_Timeframes()
    {
        var response = await _client.GetAsync("/api/admin/candle-sync/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be(TimeframeSyncStatus.Running);
        root.GetProperty("readyTimeframes").GetInt32().Should().Be(1);
        root.GetProperty("runningTimeframes").GetInt32().Should().Be(1);
        root.GetProperty("failedTimeframes").GetInt32().Should().Be(1);
        root.GetProperty("timeframes").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Candle_Sync_Status_By_Timeframe_Returns_Row()
    {
        var response = await _client.GetAsync("/api/admin/candle-sync/status/5m");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("timeframe").GetString().Should().Be("5m");
        root.GetProperty("status").GetString().Should().Be(TimeframeSyncStatus.Running);
        root.GetProperty("syncMode").GetString().Should().Be(TimeframeSyncMode.OfflineGapRecovery);
    }

    [Fact]
    public async Task Health_Includes_HistorySync_Summary()
    {
        var response = await _client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var historySync = doc.RootElement.GetProperty("historySync");

        historySync.GetProperty("status").GetString().Should().Be(TimeframeSyncStatus.Running);
        historySync.GetProperty("readyTimeframes").GetInt32().Should().Be(1);
        historySync.GetProperty("failedTimeframes").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Ml_Diagnostics_Returns_Data_Quality_Report()
    {
        var response = await _client.GetAsync("/api/admin/ml/diagnostics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("overallStatus").GetString().Should().Be(MlDiagnosticsStatus.Warning);
        root.GetProperty("timeframe").GetString().Should().Be("ALL");
        root.GetProperty("labelQuality").GetProperty("inconsistentPnlLabels").GetInt32().Should().Be(2);
        root.GetProperty("featureDrift").GetProperty("topFeatures").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Adaptive_Status_Returns_Timeframe_Profiles_And_Recent_Changes()
    {
        var parameters = StrategyParameters.Default with
        {
            TimeframeProfiles = TimeframeStrategyProfileSet.Recommended
        };

        var adaptive = new MarketAdaptiveParameterService(new Mock<ILogger<MarketAdaptiveParameterService>>().Object);
        var m5Snapshot = MakeAdaptiveSnapshot("5m");
        var h1Snapshot = MakeAdaptiveSnapshot("1h");
        var regime = MakeAdaptiveRegime();
        adaptive.AdaptParameters(parameters, m5Snapshot, regime, MakeAdaptiveCandle(DateTimeOffset.UtcNow.AddMinutes(-5)), Timeframe.M5, [m5Snapshot]);
        adaptive.AdaptParameters(parameters, h1Snapshot, regime, MakeAdaptiveCandle(DateTimeOffset.UtcNow.AddHours(-1)), Timeframe.H1, [h1Snapshot]);
        adaptive.RecordOutcome(new SignalOutcome
        {
            SignalId = Guid.NewGuid(),
            OutcomeLabel = OutcomeLabel.WIN,
            PnlR = 1.2m,
            EvaluatedAtUtc = DateTimeOffset.UtcNow
        }, "5m", "NORMAL_MODERATE", parameters);

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<MarketAdaptiveParameterService>();
                services.RemoveAll<IParameterProvider>();

                var provider = new Mock<IParameterProvider>();
                provider.Setup(p => p.GetActive()).Returns(parameters);
                provider.Setup(p => p.RefreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
                provider.Setup(p => p.ForceOverrideMlMode(It.IsAny<MlMode>()));

                services.AddSingleton(adaptive);
                services.AddSingleton(provider.Object);
            });
        }).CreateClient();

        var response = await client.GetAsync("/api/admin/adaptive/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("timeframeProfiles").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        root.GetProperty("recentChanges").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        root.GetProperty("conditionDetails").EnumerateArray()
            .Should().Contain(detail => detail.GetProperty("timeframe").GetString() == "5m");
        root.GetProperty("timeframeProfiles").EnumerateArray()
            .Should().Contain(profile => profile.GetProperty("timeframe").GetString() == "1h");
    }

    [Fact]
    public async Task Parameter_Set_Overview_Returns_Base_Set_And_Timeframe_Runtime_Setups()
    {
        var parameters = StrategyParameters.Default with
        {
            TimeframeProfiles = TimeframeStrategyProfileSet.Recommended
        };
        var activatedAt = new DateTimeOffset(2026, 4, 21, 20, 47, 0, TimeSpan.Zero);
        var baseSet = new StrategyParameterSet
        {
            Id = 21,
            StrategyVersion = parameters.StrategyVersion,
            ParameterHash = "base-overview-hash",
            Parameters = parameters,
            Status = ParameterSetStatus.Active,
            CreatedBy = "tests",
            ActivatedUtc = activatedAt,
            Notes = "overview baseline"
        };

        var adaptive = new MarketAdaptiveParameterService(new Mock<ILogger<MarketAdaptiveParameterService>>().Object);
        var m5Snapshot = MakeAdaptiveSnapshot("5m");
        var h1Snapshot = MakeAdaptiveSnapshot("1h");
        var regime = MakeAdaptiveRegime();
        adaptive.AdaptParameters(parameters, m5Snapshot, regime, MakeAdaptiveCandle(DateTimeOffset.UtcNow.AddMinutes(-5)), Timeframe.M5, [m5Snapshot]);
        adaptive.AdaptParameters(parameters, h1Snapshot, regime, MakeAdaptiveCandle(DateTimeOffset.UtcNow.AddHours(-1)), Timeframe.H1, [h1Snapshot]);

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<MarketAdaptiveParameterService>();
                services.RemoveAll<IParameterProvider>();
                services.RemoveAll<IParameterRepository>();

                var provider = new Mock<IParameterProvider>();
                provider.Setup(p => p.GetActive()).Returns(parameters);
                provider.Setup(p => p.RefreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
                provider.Setup(p => p.ForceOverrideMlMode(It.IsAny<MlMode>()));

                var repository = new Mock<IParameterRepository>();
                repository.Setup(r => r.GetActiveAsync(parameters.StrategyVersion, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(baseSet);

                services.AddSingleton(adaptive);
                services.AddSingleton(provider.Object);
                services.AddSingleton(repository.Object);
            });
        }).CreateClient();

        var response = await client.GetAsync("/api/admin/parameter-sets/overview");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("baseSet").GetProperty("id").GetInt64().Should().Be(21);
        root.GetProperty("baseActivatedUtc").GetDateTimeOffset().Should().Be(activatedAt);
        root.GetProperty("timeframeSetupCount").GetInt32().Should().BeGreaterThanOrEqualTo(2);
        root.GetProperty("timeframeSetups").EnumerateArray()
            .Should().Contain(profile => profile.GetProperty("timeframe").GetString() == "5m");
        root.GetProperty("latestAdaptiveChangeUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Admin_Endpoint_Rejects_NonLoopback_RemoteIp()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/ml/health");
        request.Headers.Add("X-Test-Remote-Ip", "8.8.8.8");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_Endpoint_DoesNotTrust_XForwardedFor_Loopback_Header()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/ml/health");
        request.Headers.Add("X-Test-Remote-Ip", "8.8.8.8");
        request.Headers.Add("X-Forwarded-For", "127.0.0.1");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Dashboard_Latest_Uses_Actual_Latest_Signal_Across_Timeframes()
    {
        var latestOverall = new SignalRecommendation
        {
            SignalId = Guid.NewGuid(),
            Symbol = "ETHUSD",
            Timeframe = "1m",
            SignalTimeUtc = new DateTimeOffset(2026, 4, 10, 10, 5, 0, TimeSpan.Zero),
            Direction = SignalDirection.SELL,
            EntryPrice = 2210m,
            TpPrice = 2204m,
            SlPrice = 2213m,
            RiskPercent = 0.5m,
            RiskUsd = 10m,
            ConfidenceScore = 81,
            Regime = Regime.BEARISH,
            StrategyVersion = "v3.0",
            Reasons = ["Latest overall signal"],
            Status = SignalStatus.OPEN
        };

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISignalRepository>();

                var mock = new Mock<ISignalRepository>();
                mock.Setup(r => r.GetLatestSignalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(latestOverall);
                mock.Setup(r => r.GetLatestPrimaryTimeframeSignalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((string _, string tf, CancellationToken _) => tf == "5m"
                        ? new SignalRecommendation
                        {
                            SignalId = Guid.NewGuid(),
                            Symbol = "ETHUSD",
                            Timeframe = "5m",
                            SignalTimeUtc = new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero),
                            Direction = SignalDirection.BUY,
                            EntryPrice = 2205m,
                            TpPrice = 2212m,
                            SlPrice = 2201m,
                            RiskPercent = 0.5m,
                            RiskUsd = 10m,
                            ConfidenceScore = 74,
                            Regime = Regime.BULLISH,
                            StrategyVersion = "v3.0",
                            Reasons = ["Primary timeframe signal"],
                            Status = SignalStatus.OPEN
                        }
                        : latestOverall);
                mock.Setup(r => r.GetSignalHistoryAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<SignalRecommendation>());
                mock.Setup(r => r.GetSignalHistoryWithOutcomesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<SignalWithOutcome>());
                mock.Setup(r => r.GetSignalCountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(0);
                mock.Setup(r => r.GetOutcomesAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<SignalOutcome>());

                services.AddSingleton(mock.Object);
            });
        }).CreateClient();

        var response = await client.GetAsync("/api/dashboard/latest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var signal = doc.RootElement.GetProperty("signal");
        signal.GetProperty("timeframe").GetString().Should().Be("1m");
        signal.GetProperty("direction").GetString().Should().Be("SELL");
    }

    [Fact]
    public async Task Dashboard_Latest_Returns_Linked_Decision_Including_Timeframe()
    {
        var signalEvaluationId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var linkedDecisionId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var unrelatedEvaluationId = Guid.Parse("99999999-9999-9999-9999-999999999999");

        var latestSignal = new SignalRecommendation
        {
            SignalId = Guid.NewGuid(),
            EvaluationId = signalEvaluationId,
            Symbol = "ETHUSD",
            Timeframe = "5m",
            SignalTimeUtc = new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero),
            Direction = SignalDirection.BUY,
            EntryPrice = 2205m,
            TpPrice = 2212m,
            SlPrice = 2201m,
            RiskPercent = 0.5m,
            RiskUsd = 10m,
            ConfidenceScore = 74,
            Regime = Regime.BULLISH,
            StrategyVersion = "v3.0",
            Reasons = ["Primary timeframe signal"],
            Status = SignalStatus.OPEN
        };

        var linkedDecision = new SignalDecision
        {
            DecisionId = linkedDecisionId,
            EvaluationId = signalEvaluationId,
            Symbol = "ETHUSD",
            Timeframe = "5m",
            DecisionTimeUtc = new DateTimeOffset(2026, 4, 10, 10, 0, 5, TimeSpan.Zero),
            BarTimeUtc = new DateTimeOffset(2026, 4, 10, 9, 55, 0, TimeSpan.Zero),
            DecisionType = SignalDirection.BUY,
            OutcomeCategory = OutcomeCategory.SIGNAL_GENERATED,
            LifecycleState = SignalLifecycleState.PERSISTED,
            Origin = DecisionOrigin.CLOSED_BAR,
            UsedRegime = Regime.BULLISH,
            ReasonCodes = [RejectReasonCode.SCORE_BELOW_THRESHOLD],
            ReasonDetails = ["linked decision"],
            ConfidenceScore = 74,
            IndicatorSnapshot = new Dictionary<string, decimal>(),
            SourceMode = SourceMode.LIVE
        };

        var unrelatedLatestDecision = linkedDecision with
        {
            DecisionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            EvaluationId = unrelatedEvaluationId,
            Timeframe = "1m",
            DecisionTimeUtc = new DateTimeOffset(2026, 4, 10, 10, 1, 0, TimeSpan.Zero),
            OutcomeCategory = OutcomeCategory.OPERATIONAL_BLOCKED,
            ReasonDetails = ["unrelated later decision"]
        };

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISignalRepository>();
                services.RemoveAll<IDecisionAuditRepository>();

                var signalRepo = new Mock<ISignalRepository>();
                signalRepo.Setup(r => r.GetLatestSignalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(latestSignal);
                signalRepo.Setup(r => r.GetLatestPrimaryTimeframeSignalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(latestSignal);
                signalRepo.Setup(r => r.GetSignalHistoryAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<SignalRecommendation>());
                signalRepo.Setup(r => r.GetSignalHistoryWithOutcomesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<SignalWithOutcome>());
                signalRepo.Setup(r => r.GetSignalCountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(0);
                signalRepo.Setup(r => r.GetOutcomesAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<SignalOutcome>());

                var decisionRepo = new Mock<IDecisionAuditRepository>();
                decisionRepo.Setup(r => r.GetDecisionByEvaluationIdAsync(signalEvaluationId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(linkedDecision);
                decisionRepo.Setup(r => r.GetLatestDecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(unrelatedLatestDecision);
                decisionRepo.Setup(r => r.GetDecisionsAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<SignalDecision>());
                decisionRepo.Setup(r => r.GetSummaryAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new DecisionSummary());

                services.AddSingleton(signalRepo.Object);
                services.AddSingleton(decisionRepo.Object);
            });
        }).CreateClient();

        var response = await client.GetAsync("/api/dashboard/latest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var decision = doc.RootElement.GetProperty("decision");
        decision.GetProperty("decisionId").GetGuid().Should().Be(linkedDecisionId);
        decision.GetProperty("evaluationId").GetGuid().Should().Be(signalEvaluationId);
        decision.GetProperty("timeframe").GetString().Should().Be("5m");

        var latestDecision = doc.RootElement.GetProperty("latestDecision");
        latestDecision.GetProperty("evaluationId").GetGuid().Should().Be(unrelatedEvaluationId);
        latestDecision.GetProperty("timeframe").GetString().Should().Be("1m");
    }

    private static IndicatorSnapshot MakeAdaptiveSnapshot(string timeframe) => new()
    {
        Symbol = "ETHUSD",
        Timeframe = timeframe,
        CandleOpenTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        Ema20 = 2090,
        Ema50 = 2085,
        Rsi14 = 52,
        Macd = 0.5m,
        MacdSignal = 0.3m,
        MacdHist = 0.2m,
        Atr14 = 10m,
        Adx14 = 22,
        PlusDi = 25,
        MinusDi = 15,
        VolumeSma20 = 100,
        Vwap = 2080,
        Spread = 1m,
        CloseMid = 2100,
        MidHigh = 2105,
        MidLow = 2095,
        IsProvisional = false
    };

    private static RegimeResult MakeAdaptiveRegime() => new()
    {
        Symbol = "ETHUSD",
        CandleOpenTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-15),
        Regime = Regime.BULLISH,
        RegimeScore = 5,
        TriggeredConditions = ["all"],
        DisqualifyingConditions = []
    };

    private static RichCandle MakeAdaptiveCandle(DateTimeOffset openTime) => new()
    {
        OpenTime = openTime,
        BidOpen = 2097,
        BidHigh = 2105,
        BidLow = 2095,
        BidClose = 2100,
        AskOpen = 2098,
        AskHigh = 2106,
        AskLow = 2096,
        AskClose = 2101,
        Volume = 150,
        IsClosed = true
    };

    // ─── Test fixture with mocked services ────────────────
    public class TestApp : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            // Provide required config values so startup validation passes
            builder.UseSetting("CAPITAL_BASE_URL", "https://demo-api-capital.backend-capital.com");
            builder.UseSetting("CAPITAL_API_KEY", "test-key");
            builder.UseSetting("CAPITAL_IDENTIFIER", "test-id");
            builder.UseSetting("CAPITAL_PASSWORD", "test-pass");

            builder.ConfigureServices(services =>
            {
                // Remove hosted services to avoid real API calls / DB connections
                services.RemoveAll<DataIngestionService>();
                services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();

                // Replace real services with mocks
                services.RemoveAll<ICapitalClient>();
                services.RemoveAll<ICapitalTradingClient>();
                services.RemoveAll<IDbMigrator>();
                services.RemoveAll<ICandleRepository>();
                services.RemoveAll<ICandleSyncRepository>();
                services.RemoveAll<IAuditRepository>();
                services.RemoveAll<IIndicatorRepository>();
                services.RemoveAll<IRegimeRepository>();
                services.RemoveAll<ISignalRepository>();
                services.RemoveAll<IDecisionAuditRepository>();
                services.RemoveAll<IMlPredictionRepository>();
                services.RemoveAll<IMlDataDiagnosticsService>();
                services.RemoveAll<IMlDataDiagnosticsRepository>();
                services.RemoveAll<IPortalOverridesRepository>();
                services.RemoveAll<IBlockedSignalHistoryService>();
                services.RemoveAll<IGeneratedSignalHistoryService>();
                services.RemoveAll<IExecutedTradeRepository>();
                services.RemoveAll<ITradeExecutionService>();
                services.RemoveAll<ITradeExecutionQueueService>();
                services.RemoveAll<ITradeExecutionPolicy>();
                services.RemoveAll<IExecutedTradeResetService>();
                services.RemoveAll<IAccountSnapshotService>();
                services.RemoveAll<TradeExecutionRuntimeState>();
                services.RemoveAll<BackfillService>();
                services.RemoveAll<LiveTickProcessor>();
                services.RemoveAll<CandleSyncState>();
                services.RemoveAll<IParameterRepository>();
                services.RemoveAll<IParameterProvider>();

                var capitalClient = CreateMockCapitalClient();
                services.AddSingleton(capitalClient);
                services.AddSingleton<ICapitalTradingClient>(capitalClient);
                services.AddSingleton<IDbMigrator>(new NoOpDbMigrator());
                services.AddSingleton(CreateMockCandleRepo());
                services.AddSingleton(CreateMockCandleSyncRepo());
                services.AddSingleton(CreateMockAuditRepo());
                services.AddSingleton(CreateMockIndicatorRepo());
                services.AddSingleton(CreateMockRegimeRepo());
                services.AddSingleton(CreateMockSignalRepo());
                services.AddSingleton(CreateMockDecisionAuditRepo());
                services.AddSingleton(CreateMockMlPredictionRepo());
                services.AddSingleton(CreateMockMlDiagnosticsService());
                services.AddSingleton(CreateMockPortalOverridesRepository());
                services.AddSingleton(CreateMockBlockedSignalHistoryService());
                services.AddSingleton(CreateMockGeneratedSignalHistoryService());
                services.AddSingleton(CreateMockExecutedTradeRepository());
                services.AddSingleton(CreateMockTradeExecutionService());
                services.AddSingleton(CreateMockTradeExecutionQueueService());
                services.AddSingleton(CreateMockTradeExecutionPolicy());
                services.AddSingleton(CreateMockExecutedTradeResetService());
                services.AddSingleton(CreateMockAccountSnapshotService());
                services.AddSingleton(CreateMockParameterRepository());
                services.AddSingleton(CreateMockParameterProvider());
                var runtimeState = new TradeExecutionRuntimeState();
                runtimeState.RecordSync(true, "Mock note");
                services.AddSingleton(runtimeState);
                services.AddSingleton(CreateSeededCandleSyncState());
                services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>();
            });
        }

        private sealed class NoOpDbMigrator : IDbMigrator
        {
            public Task MigrateAsync(CancellationToken ct = default) => Task.CompletedTask;
        }

        private sealed class TestRemoteIpStartupFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
                => app =>
                {
                    app.Use(async (ctx, nextMiddleware) =>
                    {
                        if (ctx.Request.Headers.TryGetValue("X-Test-Remote-Ip", out var values)
                            && IPAddress.TryParse(values.FirstOrDefault(), out var parsed))
                        {
                            ctx.Connection.RemoteIpAddress = parsed;
                        }

                        await nextMiddleware();
                    });

                    next(app);
                };
        }

        private static IReadOnlyList<CandleSyncStatusRow> SeedSyncRows()
        {
            var startedAt = new DateTimeOffset(2026, 4, 10, 8, 0, 0, TimeSpan.Zero);
            return
            [
                new CandleSyncStatusRow(
                    Symbol: "ETHUSD",
                    Timeframe: "1m",
                    Status: TimeframeSyncStatus.Ready,
                    SyncMode: TimeframeSyncMode.EmptyBootstrap,
                    IsTableEmpty: true,
                    RequestedFromUtc: startedAt.AddDays(-30),
                    RequestedToUtc: startedAt,
                    LastExistingCandleUtc: null,
                    LastSyncedCandleUtc: startedAt.AddMinutes(-1),
                    OfflineDurationSec: 0,
                    ChunkSizeCandles: 400,
                    ChunksTotal: 5,
                    ChunksCompleted: 5,
                    LastRunStartedAtUtc: startedAt,
                    LastRunFinishedAtUtc: startedAt.AddMinutes(5),
                    LastSuccessAtUtc: startedAt.AddMinutes(5),
                    LastError: null),
                new CandleSyncStatusRow(
                    Symbol: "ETHUSD",
                    Timeframe: "5m",
                    Status: TimeframeSyncStatus.Running,
                    SyncMode: TimeframeSyncMode.OfflineGapRecovery,
                    IsTableEmpty: false,
                    RequestedFromUtc: startedAt.AddHours(-2),
                    RequestedToUtc: startedAt,
                    LastExistingCandleUtc: startedAt.AddHours(-2).AddMinutes(-5),
                    LastSyncedCandleUtc: startedAt.AddMinutes(-10),
                    OfflineDurationSec: 7200,
                    ChunkSizeCandles: 400,
                    ChunksTotal: 3,
                    ChunksCompleted: 2,
                    LastRunStartedAtUtc: startedAt,
                    LastRunFinishedAtUtc: null,
                    LastSuccessAtUtc: null,
                    LastError: null),
                new CandleSyncStatusRow(
                    Symbol: "ETHUSD",
                    Timeframe: "15m",
                    Status: TimeframeSyncStatus.Failed,
                    SyncMode: TimeframeSyncMode.OfflineGapRecovery,
                    IsTableEmpty: false,
                    RequestedFromUtc: startedAt.AddHours(-6),
                    RequestedToUtc: startedAt,
                    LastExistingCandleUtc: startedAt.AddHours(-6).AddMinutes(-15),
                    LastSyncedCandleUtc: startedAt.AddHours(-1),
                    OfflineDurationSec: 21600,
                    ChunkSizeCandles: 400,
                    ChunksTotal: 2,
                    ChunksCompleted: 1,
                    LastRunStartedAtUtc: startedAt,
                    LastRunFinishedAtUtc: startedAt.AddMinutes(1),
                    LastSuccessAtUtc: null,
                    LastError: "429 rate limit")
            ];
        }

        private static ICapitalClient CreateMockCapitalClient()
        {
            var mock = new Mock<ICapitalClient>();
            mock.SetupGet(c => c.IsDemoEnvironment).Returns(true);
            mock.Setup(c => c.EnsureDemoReadyAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            mock.Setup(c => c.GetSpotPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SpotPrice(2050m, 2051m, 2050.5m, DateTimeOffset.UtcNow));
            mock.Setup(c => c.GetOpenPositionsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<CapitalPositionSnapshot>());
            mock.Setup(c => c.GetPositionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((CapitalPositionSnapshot?)null);
            mock.Setup(c => c.GetActivityHistoryAsync(It.IsAny<CapitalActivityQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<CapitalActivityRecord>());
            return mock.Object;
        }

        private static IParameterRepository CreateMockParameterRepository()
        {
            var defaults = StrategyParameters.Default;
            var active = new StrategyParameterSet
            {
                Id = 1,
                StrategyVersion = defaults.StrategyVersion,
                ParameterHash = "test-default",
                Parameters = defaults,
                Status = ParameterSetStatus.Active,
                CreatedBy = "tests",
                Notes = "Test active parameter set"
            };

            var mock = new Mock<IParameterRepository>();
            mock.Setup(r => r.GetActiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(active);
            mock.Setup(r => r.GetByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(active);
            mock.Setup(r => r.InsertAsync(It.IsAny<StrategyParameterSet>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(2);
            mock.Setup(r => r.ActivateAsync(It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            mock.Setup(r => r.UpdateStatusAsync(It.IsAny<long>(), It.IsAny<ParameterSetStatus>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            mock.Setup(r => r.GetCandidatesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([active]);
            return mock.Object;
        }

        private static IParameterProvider CreateMockParameterProvider()
        {
            var defaults = StrategyParameters.Default;
            var cached = defaults;

            var mock = new Mock<IParameterProvider>();
            mock.Setup(p => p.GetActive()).Returns(() => cached);
            mock.Setup(p => p.RefreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            mock.Setup(p => p.ForceOverrideMlMode(It.IsAny<MlMode>()))
                .Callback<MlMode>(mode => cached = cached with { MlMode = mode });
            return mock.Object;
        }

        private static IExecutedTradeRepository CreateMockExecutedTradeRepository()
        {
            var snapshot = new AccountSnapshot
            {
                SnapshotId = 1,
                AccountId = "demo-1",
                AccountName = "DEMOAI",
                Currency = "USD",
                Balance = 10000m,
                Equity = 10010m,
                Available = 9200m,
                Margin = 500m,
                Funds = 10010m,
                OpenPositions = 1,
                IsDemo = true,
                HedgingMode = false,
                CapturedAtUtc = new DateTimeOffset(2026, 4, 10, 11, 0, 0, TimeSpan.Zero)
            };

            var trade = new ExecutedTrade
            {
                ExecutedTradeId = 7,
                SignalId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                SourceType = SignalExecutionSourceType.Recommended,
                Symbol = "ETHUSD",
                Instrument = "ETHUSD",
                Timeframe = "5m",
                Direction = SignalDirection.BUY,
                RecommendedEntryPrice = 2200m,
                ActualEntryPrice = 2201m,
                TpPrice = 2208m,
                SlPrice = 2196m,
                RequestedSize = 0.1m,
                ExecutedSize = 0.1m,
                DealReference = "DEAL-REF-1",
                DealId = "DEAL-ID-1",
                Status = ExecutedTradeStatus.Open,
                AccountId = "demo-1",
                AccountName = "DEMOAI",
                IsDemo = true,
                AccountCurrency = "USD",
                Pnl = 1.25m,
                OpenedAtUtc = new DateTimeOffset(2026, 4, 10, 10, 5, 0, TimeSpan.Zero),
                ClosedAtUtc = null,
                CreatedAtUtc = new DateTimeOffset(2026, 4, 10, 10, 5, 0, TimeSpan.Zero),
                UpdatedAtUtc = new DateTimeOffset(2026, 4, 10, 10, 6, 0, TimeSpan.Zero)
            };
            var generatedTrade = trade with
            {
                ExecutedTradeId = 8,
                SignalId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                SourceType = SignalExecutionSourceType.Generated,
                Status = ExecutedTradeStatus.Loss,
                Pnl = -1.0m,
                ClosedAtUtc = new DateTimeOffset(2026, 4, 10, 13, 30, 0, TimeSpan.Zero)
            };

            var mock = new Mock<IExecutedTradeRepository>();
            mock.Setup(r => r.GetExecutedTradesAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([trade]);
            mock.Setup(r => r.GetTradesForLifecycleReconciliationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<ExecutedTrade>());
            mock.Setup(r => r.GetExecutedTradeCountAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);
            mock.Setup(r => r.GetExecutedTradeAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(trade);
            mock.Setup(r => r.GetBySourceSignalAsync(It.IsAny<Guid>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid signalId, SignalExecutionSourceType sourceType, CancellationToken _) =>
                {
                    if (signalId == trade.SignalId && sourceType == trade.SourceType) return trade;
                    if (signalId == generatedTrade.SignalId && sourceType == generatedTrade.SourceType) return generatedTrade;
                    return null;
                });
            mock.Setup(r => r.GetLatestBySourceSignalsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<SignalExecutionSourceType>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> signalIds, SignalExecutionSourceType sourceType, CancellationToken _) =>
                {
                    var result = new Dictionary<Guid, ExecutedTrade>();
                    if (sourceType == trade.SourceType && signalIds.Contains(trade.SignalId))
                        result[trade.SignalId] = trade;
                    if (sourceType == generatedTrade.SourceType && signalIds.Contains(generatedTrade.SignalId))
                        result[generatedTrade.SignalId] = generatedTrade;
                    return (IReadOnlyDictionary<Guid, ExecutedTrade>)result;
                });
            mock.Setup(r => r.GetExecutionStatsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ExecutedTradeStats
                {
                    TotalExecuted = 1,
                    OpenTrades = 1,
                    Wins = 1,
                    Losses = 0,
                    FailedExecutions = 0,
                    TotalPnl = 1.25m,
                    WinRate = 100m,
                    Currency = "USD"
                });
            mock.Setup(r => r.GetExecutionStatsAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ExecutedTradeStats
                {
                    TotalExecuted = 1,
                    OpenTrades = 1,
                    Wins = 1,
                    Losses = 0,
                    FailedExecutions = 0,
                    TotalPnl = 1.25m,
                    WinRate = 100m,
                    Currency = "USD"
                });
            mock.Setup(r => r.GetLatestAccountSnapshotAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(snapshot);
            mock.Setup(r => r.GetLatestAccountSnapshotAsync(It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(snapshot);
            mock.Setup(r => r.GetActiveExecutedTradeCountAsync(It.IsAny<ExecutedTradeQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);
            return mock.Object;
        }

        private static ITradeExecutionService CreateMockTradeExecutionService()
        {
            var mock = new Mock<ITradeExecutionService>();
            mock.Setup(s => s.ForceCloseAsync(It.IsAny<long>(), It.IsAny<ForceCloseRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ForceCloseResult
                {
                    Success = true,
                    Message = "Closed",
                    DealReference = "CLOSE-REF-1",
                    DealId = "DEAL-ID-1",
                    CloseLevel = 2202m,
                    Pnl = 1.5m
                });
            mock.Setup(s => s.ExecuteAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TradeExecutionResult
                {
                    Success = true,
                    ExecutedTradeId = 7,
                    Status = ExecutedTradeStatus.Open,
                    DealReference = "DEAL-REF-1",
                    DealId = "DEAL-ID-1",
                    ActualEntryPrice = 2201m,
                    ExecutedSize = 0.1m,
                    Message = "Mock execution succeeded"
                });
            return mock.Object;
        }

        private static ITradeExecutionQueueService CreateMockTradeExecutionQueueService()
        {
            var mock = new Mock<ITradeExecutionQueueService>();
            mock.Setup(s => s.EnqueueAsync(It.IsAny<TradeExecutionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TradeExecutionQueueResult
                {
                    Accepted = true,
                    QueueEntryId = 7,
                    Status = TradeExecutionQueueStatus.Queued.ToString(),
                    Message = "Mock queue accepted"
                });
            mock.Setup(s => s.DrainAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(0);
            mock.Setup(s => s.GetSnapshotAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TradeExecutionQueueSnapshot
                {
                    ServerTimeUtc = new DateTimeOffset(2026, 4, 10, 11, 15, 0, TimeSpan.Zero),
                    ActiveTradeCount = 2,
                    BrokerOpenTradeCount = 1,
                    PendingSubmissionCount = 1,
                    MaxConcurrentOpenTrades = 3,
                    QueueConcurrentRequestLimit = 3,
                    AvailableDispatchSlots = 1,
                    QueuedCount = 1,
                    ProcessingCount = 0,
                    CompletedCount = 9,
                    FailedCount = 0,
                    Entries =
                    [
                        new TradeExecutionQueueEntrySnapshot
                        {
                            QueueEntryId = 71,
                            SignalId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                            EvaluationId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                            SourceType = SignalExecutionSourceType.Recommended,
                            RequestedBy = "auto-executor",
                            RequestedSize = 0.05m,
                            ForceMarketExecution = false,
                            Status = TradeExecutionQueueStatus.Queued,
                            CreatedAtUtc = new DateTimeOffset(2026, 4, 10, 11, 10, 0, TimeSpan.Zero),
                            UpdatedAtUtc = new DateTimeOffset(2026, 4, 10, 11, 10, 0, TimeSpan.Zero),
                            AgeSeconds = 300,
                            WaitSeconds = 300
                        }
                    ]
                });
            mock.Setup(s => s.WaitForWorkAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            mock.Setup(s => s.NotifyWorkAvailable());
            return mock.Object;
        }

        private static IExecutedTradeResetService CreateMockExecutedTradeResetService()
        {
            var mock = new Mock<IExecutedTradeResetService>();
            mock.Setup(s => s.ResetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ExecutedTradeResetResult
                {
                    ResetAtUtc = new DateTimeOffset(2026, 4, 10, 11, 20, 0, TimeSpan.Zero),
                    QueueEntriesCleared = 2,
                    ExecutedTradesCleared = 3,
                    ExecutionAttemptsCleared = 4,
                    ExecutionEventsCleared = 5,
                    AccountSnapshotsCleared = 1,
                    CloseActionsCleared = 1
                });
            return mock.Object;
        }

        private static ITradeExecutionPolicy CreateMockTradeExecutionPolicy()
        {
            var mock = new Mock<ITradeExecutionPolicy>();
            mock.Setup(p => p.GetSettings()).Returns(new TradeExecutionPolicySettings
            {
                Enabled = true,
                AutoExecuteEnabled = false,
                DemoOnly = true,
                AllowedSourceTypes = new HashSet<SignalExecutionSourceType>
                {
                    SignalExecutionSourceType.Recommended,
                    SignalExecutionSourceType.Generated,
                    SignalExecutionSourceType.Blocked
                }
            });
            return mock.Object;
        }

        private static IAccountSnapshotService CreateMockAccountSnapshotService()
        {
            var mock = new Mock<IAccountSnapshotService>();
            mock.Setup(s => s.GetLatestAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccountSnapshot
                {
                    SnapshotId = 1,
                    AccountId = "demo-1",
                    AccountName = "DEMOAI",
                    Currency = "USD",
                    Balance = 10000m,
                    Equity = 10010m,
                    Available = 9200m,
                    Margin = 500m,
                    Funds = 10010m,
                    OpenPositions = 1,
                    IsDemo = true,
                    HedgingMode = false,
                    CapturedAtUtc = new DateTimeOffset(2026, 4, 10, 11, 0, 0, TimeSpan.Zero)
                });
            return mock.Object;
        }

        private static ICandleRepository CreateMockCandleRepo()
        {
            var mock = new Mock<ICandleRepository>();
            mock.Setup(r => r.GetClosedCandlesAsync(It.IsAny<Timeframe>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<RichCandle>());
            return mock.Object;
        }

        private static ICandleSyncRepository CreateMockCandleSyncRepo()
        {
            var rows = SeedSyncRows();
            var mock = new Mock<ICandleSyncRepository>();
            mock.Setup(r => r.GetAllAsync("ETHUSD", It.IsAny<CancellationToken>()))
                .ReturnsAsync(rows);
            mock.Setup(r => r.GetAsync("ETHUSD", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string timeframe, CancellationToken _) =>
                    rows.FirstOrDefault(r => r.Timeframe == timeframe));
            return mock.Object;
        }

        private static IIndicatorRepository CreateMockIndicatorRepo()
        {
            var mock = new Mock<IIndicatorRepository>();
            mock.Setup(r => r.GetLatestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IndicatorSnapshot?)null);
            mock.Setup(r => r.GetSnapshotsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<IndicatorSnapshot>());
            return mock.Object;
        }

        private static IRegimeRepository CreateMockRegimeRepo()
        {
            var mock = new Mock<IRegimeRepository>();
            mock.Setup(r => r.GetLatestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((RegimeResult?)null);
            return mock.Object;
        }

        private static IAuditRepository CreateMockAuditRepo()
        {
            var mock = new Mock<IAuditRepository>();
            mock.Setup(r => r.HasRecentUnresolvedGapsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            mock.Setup(r => r.ResolveOldGapsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(0);
            mock.Setup(r => r.GetGapDiagnosticsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GapDiagnostics(0, 0, null, null));
            return mock.Object;
        }

        private static ISignalRepository CreateMockSignalRepo()
        {
            var latestPrimary = new SignalRecommendation
            {
                SignalId = Guid.NewGuid(),
                EvaluationId = Guid.Parse("12121212-1212-1212-1212-121212121212"),
                Symbol = "ETHUSD",
                Timeframe = "5m",
                SignalTimeUtc = new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero),
                Direction = SignalDirection.BUY,
                EntryPrice = 2205m,
                TpPrice = 2212m,
                SlPrice = 2201m,
                RiskPercent = 0.5m,
                RiskUsd = 10m,
                ConfidenceScore = 74,
                Regime = Regime.BULLISH,
                StrategyVersion = "v3.0",
                Reasons = ["Primary timeframe signal"],
                Status = SignalStatus.OPEN
            };
            var mock = new Mock<ISignalRepository>();
            mock.Setup(r => r.GetLatestSignalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(latestPrimary);
            mock.Setup(r => r.GetLatestPrimaryTimeframeSignalAsync(It.IsAny<string>(), "5m", It.IsAny<CancellationToken>()))
                .ReturnsAsync(latestPrimary);
            mock.Setup(r => r.GetSignalHistoryAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<SignalRecommendation>());
            mock.Setup(r => r.GetSignalHistoryWithOutcomesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new SignalWithOutcome
                    {
                        Signal = new SignalRecommendation
                        {
                            SignalId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                            EvaluationId = Guid.Parse("13131313-1313-1313-1313-131313131313"),
                            Symbol = "ETHUSD",
                            Timeframe = "5m",
                            SignalTimeUtc = new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero),
                            Direction = SignalDirection.BUY,
                            EntryPrice = 2205m,
                            TpPrice = 2212m,
                            SlPrice = 2201m,
                            RiskPercent = 0.5m,
                            RiskUsd = 10m,
                            ConfidenceScore = 74,
                            Regime = Regime.BULLISH,
                            StrategyVersion = "v3.0",
                            Reasons = ["Primary timeframe signal"],
                            Status = SignalStatus.OPEN
                        },
                        Outcome = new SignalOutcome
                        {
                            SignalId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                            OutcomeLabel = OutcomeLabel.PENDING,
                            PnlR = 0m,
                            BarsObserved = 0,
                            TpHit = false,
                            SlHit = false
                        }
                    }
                ]);
            mock.Setup(r => r.GetSignalCountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);
            mock.Setup(r => r.GetOutcomesAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<SignalOutcome>());
            return mock.Object;
        }

        private static IDecisionAuditRepository CreateMockDecisionAuditRepo()
        {
            var mock = new Mock<IDecisionAuditRepository>();
            mock.Setup(r => r.GetLatestDecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SignalDecision?)null);
            mock.Setup(r => r.GetDecisionByEvaluationIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SignalDecision?)null);
            mock.Setup(r => r.GetDecisionsAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<SignalDecision>());
            mock.Setup(r => r.GetSummaryAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DecisionSummary
                {
                    TotalDecisions = 0,
                    LongCount = 0,
                    ShortCount = 0,
                    NoTradeCount = 0,
                    StrategyNoTradeCount = 0,
                    OperationalBlockedCount = 0,
                    ContextNotReadyCount = 0,
                    LastSignalTime = null,
                    LastEvaluationTime = null,
                    TopRejectReasons = []
                });
            return mock.Object;
        }

        private static IMlPredictionRepository CreateMockMlPredictionRepo()
        {
            var mock = new Mock<IMlPredictionRepository>();
            mock.Setup(r => r.GetBySignalIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((MlPrediction?)null);
            mock.Setup(r => r.GetLatestAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((MlPrediction?)null);
            mock.Setup(r => r.GetRecentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<MlPrediction>());
            return mock.Object;
        }

        private static IMlDataDiagnosticsService CreateMockMlDiagnosticsService()
        {
            var mock = new Mock<IMlDataDiagnosticsService>();
            mock.Setup(s => s.GetReportAsync("ETHUSD", It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MlDataDiagnosticsReport
                {
                    GeneratedAtUtc = new DateTimeOffset(2026, 4, 10, 9, 0, 0, TimeSpan.Zero),
                    Symbol = "ETHUSD",
                    Timeframe = "ALL",
                    OverallStatus = MlDiagnosticsStatus.Warning,
                    FeatureVersion = "v3.0",
                    IsFeatureVersionFallback = false,
                    Model = new MlModelDiagnosticsSummary
                    {
                        ModelVersion = "model-v2",
                        TrainingSampleCount = 220,
                        FeatureCount = 74,
                        CurrentFeatureCount = 74,
                        UsesCurrentFeatureContract = true,
                        AucRoc = 0.81m,
                        BrierScore = 0.17m,
                        ExpectedCalibrationError = 0.05m,
                        LogLoss = 0.52m
                    },
                    LabelQuality = new MlLabelQualityDiagnostics
                    {
                        Status = MlDiagnosticsStatus.Warning,
                        TotalOutcomes = 240,
                        LabeledOutcomes = 220,
                        PendingOutcomes = 10,
                        ExpiredOutcomes = 5,
                        AmbiguousOutcomes = 5,
                        InconsistentPnlLabels = 2,
                        ConflictingTpSlHits = 0,
                        ClosedTimestampMissing = 0,
                        TotalFeatureSnapshots = 230,
                        LinkedFeatureSnapshots = 220,
                        LabeledFeatureSnapshots = 210,
                        PendingLinkSnapshots = 10,
                        StalePendingLinkSnapshots = 3,
                        ExpectedNoSignalSnapshots = 18,
                        MlFilteredSnapshots = 6,
                        OperationallyBlockedSnapshots = 4,
                        LinkCoveragePct = 95.6m
                    },
                    ClassBalance = new MlClassBalanceDiagnostics
                    {
                        Status = MlDiagnosticsStatus.Healthy,
                        LabeledSamples = 220,
                        Wins = 96,
                        Losses = 124,
                        WinRate = 0.436m,
                        LossToWinRatio = 1.29m,
                        ReadyForTraining = true
                    },
                    Calibration = new MlCalibrationDiagnostics
                    {
                        Status = MlDiagnosticsStatus.Warning,
                        SampleCount = 60,
                        ActiveModelSampleCount = 60,
                        ModelVersion = "model-v2",
                        UsesActiveModelOnly = true,
                        GateThreshold = 0.55m,
                        PredictedMeanWin = 0.51m,
                        ActualWinRate = 0.46m,
                        CalibrationGap = -0.05m,
                        CalibrationGapAbs = 0.05m,
                        BrierScore = 0.18m,
                        RecommendedThresholdAvg = 62m,
                        PassCount = 30,
                        PassWinRate = 0.58m,
                        FailCount = 30,
                        FailWinRate = 0.33m,
                        ThresholdLift = 0.25m
                    },
                    FeatureDrift = new MlFeatureDriftDiagnostics
                    {
                        Status = MlDiagnosticsStatus.Warning,
                        TrainingSampleCount = 220,
                        LiveSampleCount = 90,
                        LiveWindowHours = 24,
                        AveragePsi = 0.07m,
                        MaxPsi = 0.14m,
                        AverageMeanShiftSigma = 0.92m,
                        TopFeatures =
                        [
                            new MlFeatureDriftItem
                            {
                                Feature = "ema20",
                                TrainingMean = 2100m,
                                LiveMean = 2142m,
                                Psi = 0.14m,
                                MeanShiftSigma = 1.82m
                            }
                        ]
                    }
                });
            return mock.Object;
        }

        private static IBlockedSignalHistoryService CreateMockBlockedSignalHistoryService()
        {
            var mock = new Mock<IBlockedSignalHistoryService>();
            mock.Setup(s => s.GetHistoryAsync("ETHUSD", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new BlockedSignalHistoryPage
                {
                    Signals =
                    [
                        new BlockedSignalWithOutcome
                        {
                            Signal = new BlockedSignalRecommendation
                            {
                                SignalId = Guid.NewGuid(),
                                Symbol = "ETHUSD",
                                Timeframe = "5m",
                                SignalTimeUtc = new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero),
                                DecisionTimeUtc = new DateTimeOffset(2026, 4, 10, 10, 0, 5, TimeSpan.Zero),
                                BarTimeUtc = new DateTimeOffset(2026, 4, 10, 9, 55, 0, TimeSpan.Zero),
                                Direction = SignalDirection.BUY,
                                LifecycleState = SignalLifecycleState.SESSION_BLOCKED,
                                BlockReason = "Daily loss cap reached",
                                Origin = DecisionOrigin.CLOSED_BAR,
                                SourceMode = SourceMode.LIVE,
                                Regime = Regime.BULLISH,
                                StrategyVersion = "v3.1",
                                Reasons = ["Session limit blocked candidate"],
                                EntryPrice = 2200m,
                                TpPrice = 2208m,
                                SlPrice = 2195m,
                                RiskPercent = 0.5m,
                                RiskUsd = 10m,
                                ConfidenceScore = 72,
                                ExpiryBars = 60,
                                ExpiryTimeUtc = new DateTimeOffset(2026, 4, 10, 15, 0, 0, TimeSpan.Zero),
                                ExitModel = "STRUCTURE_FULL",
                                ExitExplanation = "Mocked blocked signal",
                                UsedFallbackExit = false
                            },
                            Outcome = new SignalOutcome
                            {
                                SignalId = Guid.NewGuid(),
                                BarsObserved = 12,
                                TpHit = true,
                                SlHit = false,
                                OutcomeLabel = OutcomeLabel.WIN,
                                PnlR = 1.6m,
                                MfePrice = 2209m,
                                MaePrice = 2198m,
                                MfeR = 1.7m,
                                MaeR = 0.4m,
                                ClosedAtUtc = new DateTimeOffset(2026, 4, 10, 11, 0, 0, TimeSpan.Zero)
                            }
                        }
                    ],
                    Stats = new PerformanceStats
                    {
                        TotalSignals = 1,
                        ResolvedSignals = 1,
                        Wins = 1,
                        Losses = 0,
                        Expired = 0,
                        Ambiguous = 0,
                        WinRate = 100m,
                        AverageR = 1.6m,
                        ProfitFactor = 999m,
                        TotalPnlR = 1.6m
                    },
                    Total = 1,
                    Page = 1,
                    PageSize = 5
                });
            return mock.Object;
        }

        private static IPortalOverridesRepository CreateMockPortalOverridesRepository()
        {
            PortalOverrides state = new()
            {
                RecommendedSignalExecutionEnabled = true,
                UpdatedAt = new DateTimeOffset(2026, 4, 10, 11, 0, 0, TimeSpan.Zero),
                UpdatedBy = "test"
            };

            var mock = new Mock<IPortalOverridesRepository>();
            mock.Setup(r => r.EnsureTableExistsAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            mock.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => state);
            mock.Setup(r => r.SaveAsync(It.IsAny<PortalOverrides>(), It.IsAny<CancellationToken>()))
                .Returns<PortalOverrides, CancellationToken>((overrides, _) =>
                {
                    state = state with
                    {
                        MaxOpenPositions = overrides.MaxOpenPositions ?? state.MaxOpenPositions,
                        MaxOpenPerTimeframe = overrides.MaxOpenPerTimeframe ?? state.MaxOpenPerTimeframe,
                        MaxOpenPerDirection = overrides.MaxOpenPerDirection ?? state.MaxOpenPerDirection,
                        DailyLossCapPercent = overrides.DailyLossCapPercent ?? state.DailyLossCapPercent,
                        MaxConsecutiveLossesPerDay = overrides.MaxConsecutiveLossesPerDay ?? state.MaxConsecutiveLossesPerDay,
                        ScalpMaxConsecutiveLossesPerDay = overrides.ScalpMaxConsecutiveLossesPerDay ?? state.ScalpMaxConsecutiveLossesPerDay,
                        RecommendedSignalExecutionEnabled = overrides.RecommendedSignalExecutionEnabled ?? state.RecommendedSignalExecutionEnabled,
                        UpdatedBy = overrides.UpdatedBy ?? state.UpdatedBy,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    return Task.CompletedTask;
                });
            return mock.Object;
        }

        private static IGeneratedSignalHistoryService CreateMockGeneratedSignalHistoryService()
        {
            var mock = new Mock<IGeneratedSignalHistoryService>();
            mock.Setup(s => s.GetHistoryAsync("ETHUSD", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GeneratedSignalHistoryPage
                {
                    Signals =
                    [
                        new GeneratedSignalWithOutcome
                        {
                            Signal = new GeneratedSignalRecommendation
                            {
                                SignalId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                                EvaluationId = Guid.NewGuid(),
                                Symbol = "ETHUSD",
                                Timeframe = "15m",
                                SignalTimeUtc = new DateTimeOffset(2026, 4, 10, 12, 0, 0, TimeSpan.Zero),
                                DecisionTimeUtc = new DateTimeOffset(2026, 4, 10, 12, 0, 5, TimeSpan.Zero),
                                BarTimeUtc = new DateTimeOffset(2026, 4, 10, 11, 45, 0, TimeSpan.Zero),
                                Direction = SignalDirection.SELL,
                                LifecycleState = SignalLifecycleState.CANDIDATE_CREATED,
                                Origin = DecisionOrigin.PARTIAL_RUNNING,
                                SourceMode = SourceMode.LIVE,
                                Regime = Regime.BEARISH,
                                StrategyVersion = "v3.1",
                                Reasons = ["Generated candidate"],
                                EntryPrice = 2210m,
                                TpPrice = 2202m,
                                SlPrice = 2214m,
                                RiskPercent = 0.5m,
                                RiskUsd = 10m,
                                ConfidenceScore = 76,
                                ExpiryBars = 20,
                                ExpiryTimeUtc = new DateTimeOffset(2026, 4, 10, 17, 0, 0, TimeSpan.Zero),
                                ExitModel = "STRUCTURE_FULL",
                                ExitExplanation = "Mocked generated signal",
                                UsedFallbackExit = false
                            },
                            Outcome = new SignalOutcome
                            {
                                SignalId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                                BarsObserved = 0,
                                TpHit = false,
                                SlHit = false,
                                OutcomeLabel = OutcomeLabel.PENDING,
                                PnlR = 0m,
                                MfePrice = 0m,
                                MaePrice = 0m,
                                MfeR = 0m,
                                MaeR = 0m
                            }
                        }
                    ],
                    Stats = new PerformanceStats
                    {
                        TotalSignals = 1,
                        ResolvedSignals = 1,
                        Wins = 0,
                        Losses = 1,
                        Expired = 0,
                        Ambiguous = 0,
                        WinRate = 0m,
                        AverageR = -1m,
                        ProfitFactor = 0m,
                        TotalPnlR = -1m
                    },
                    Total = 1,
                    Page = 1,
                    PageSize = 5
                });
            return mock.Object;
        }

        private static CandleSyncState CreateSeededCandleSyncState()
        {
            var state = new CandleSyncState();
            state.Set(new StartupCandleSyncSummary
            {
                Symbol = "ETHUSD",
                Status = TimeframeSyncStatus.Running,
                TotalTimeframes = 3,
                ReadyTimeframes = 1,
                FailedTimeframes = 1,
                RunningTimeframes = 1,
                NoopTimeframes = 0,
                Elapsed = TimeSpan.FromMinutes(5),
                StartedAtUtc = new DateTimeOffset(2026, 4, 10, 8, 0, 0, TimeSpan.Zero),
                FinishedAtUtc = null,
                Timeframes =
                [
                    new TimeframeSyncResult
                    {
                        Tf = Timeframe.M1,
                        Mode = TimeframeSyncMode.EmptyBootstrap,
                        Status = TimeframeSyncStatus.Ready,
                        ChunksCompleted = 5,
                        ChunksTotal = 5,
                        CandlesFetched = 5,
                        CandlesUpserted = 5,
                        LastSyncedCandleUtc = new DateTimeOffset(2026, 4, 10, 7, 59, 0, TimeSpan.Zero),
                        Elapsed = TimeSpan.FromMinutes(2)
                    },
                    new TimeframeSyncResult
                    {
                        Tf = Timeframe.M5,
                        Mode = TimeframeSyncMode.OfflineGapRecovery,
                        Status = TimeframeSyncStatus.Running,
                        ChunksCompleted = 2,
                        ChunksTotal = 3,
                        CandlesFetched = 2,
                        CandlesUpserted = 2,
                        LastSyncedCandleUtc = new DateTimeOffset(2026, 4, 10, 7, 50, 0, TimeSpan.Zero),
                        Elapsed = TimeSpan.FromMinutes(2)
                    },
                    new TimeframeSyncResult
                    {
                        Tf = Timeframe.M15,
                        Mode = TimeframeSyncMode.OfflineGapRecovery,
                        Status = TimeframeSyncStatus.Failed,
                        ChunksCompleted = 1,
                        ChunksTotal = 2,
                        CandlesFetched = 1,
                        CandlesUpserted = 1,
                        LastSyncedCandleUtc = new DateTimeOffset(2026, 4, 10, 7, 0, 0, TimeSpan.Zero),
                        Elapsed = TimeSpan.FromMinutes(1),
                        Error = "429 rate limit"
                    }
                ]
            });
            return state;
        }
    }
}
