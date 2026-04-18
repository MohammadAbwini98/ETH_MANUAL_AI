using EthSignal.Infrastructure.Apis;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EthSignal.Tests.Engine;

public class PlaywrightTickProviderTests
{
    [Fact]
    public async Task SetHeadlessModeAsync_UpdatesMode_WithoutActiveSession()
    {
        var provider = new PlaywrightTickProvider(
            NullLogger<PlaywrightTickProvider>.Instance,
            headless: true,
            browserChannel: "chrome",
            userDataDir: Path.Combine(Path.GetTempPath(), $"eth-manual-playwright-test-{Guid.NewGuid():N}"),
            manualLoginTimeoutSec: 30);

        provider.Headless.Should().BeTrue();

        var changed = await provider.SetHeadlessModeAsync(false);

        changed.Should().BeTrue();
        provider.Headless.Should().BeFalse();
    }
}
