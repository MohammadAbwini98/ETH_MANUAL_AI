using System.Net;
using System.Net.Http;
using System.Text;
using EthSignal.Infrastructure.Apis;
using FluentAssertions;

namespace EthSignal.Tests.Apis;

public sealed class CapitalClientTests
{
    [Fact]
    public async Task AuthenticateAsync_WhenSessionIsStillFresh_DoesNotReauthenticate()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.Should().EndWith("/session");

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            response.Headers.Add("CST", "cst-token");
            response.Headers.Add("X-SECURITY-TOKEN", "security-token");
            return response;
        });
        var httpClient = new HttpClient(handler);
        var sut = new CapitalClient(
            "https://demo-api-capital.backend-capital.com",
            "api-key",
            "identifier",
            "password",
            "DEMOAI",
            httpClient: httpClient);

        await sut.AuthenticateAsync();
        await sut.AuthenticateAsync();

        handler.CallCount.Should().Be(1);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }
}