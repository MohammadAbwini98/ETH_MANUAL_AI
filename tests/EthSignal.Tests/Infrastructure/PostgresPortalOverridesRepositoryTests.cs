using System.Text.Json;
using EthSignal.Infrastructure.Db;
using FluentAssertions;

namespace EthSignal.Tests.Infrastructure;

public sealed class PostgresPortalOverridesRepositoryTests
{
    [Fact]
    public void BuildMergedSettings_PreservesExistingKeysAndAddsRecommendedExecutionToggle()
    {
        var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
            {
              "maxOpenPositions": 3,
              "dailyLossCapPercent": 1.5
            }
            """)!;

        var merged = PostgresPortalOverridesRepository.BuildMergedSettings(existing, new PortalOverrides
        {
            RecommendedSignalExecutionEnabled = false
        });

        merged["maxOpenPositions"].Should().Be(3);
        merged["dailyLossCapPercent"].Should().Be(1.5m);
        merged["recommendedSignalExecutionEnabled"].Should().Be(false);
    }
}
