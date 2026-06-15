using System.Net;
using Vela.Worker.Services;

namespace Vela.Tests.Unit;

public class AlertApiExceptionTests
{
    [Fact]
    public void StatusCode_DefaultsToZero_WhenConstructedWithMessageOnly()
    {
        var ex = new AlertApiException("something went wrong");

        ex.StatusCode.Should().Be(0);
    }

    [Fact]
    public void StatusCode_DefaultsToZero_WhenConstructedWithMessageAndInner()
    {
        var inner = new HttpRequestException("transport error");
        var ex    = new AlertApiException("something went wrong", inner);

        ex.StatusCode.Should().Be(0);
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public void StatusCode_IsSet_WhenConstructedWithStatusCode(int statusCode)
    {
        var ex = new AlertApiException("HTTP error", statusCode);

        ex.StatusCode.Should().Be(statusCode);
    }

    [Fact]
    public void StatusCode_IsSet_AndInnerExceptionPreserved_WhenAllArgumentsProvided()
    {
        var inner = new Exception("original");
        var ex    = new AlertApiException("HTTP error", 401, inner);

        ex.StatusCode.Should().Be(401);
        ex.InnerException.Should().BeSameAs(inner);
    }
}

public class AlertApiClientTests
{
    // Builds an AlertApiClient backed by a handler that returns the given status and body.
    private static AlertApiClient BuildClient(HttpStatusCode statusCode, string body = "")
    {
        var httpClient = new HttpClient(new FakeHttpHandler(statusCode, body))
        {
            BaseAddress = new Uri("https://app.xtrades.net")
        };
        return new AlertApiClient(httpClient);
    }

    // -- GetAlertsAsync --

    [Fact]
    public async Task GetAlertsAsync_ReturnsEmptyList_On204NoContent()
    {
        var client = BuildClient(HttpStatusCode.NoContent);

        var result = await client.GetAlertsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAlertsAsync_ReturnsAlerts_On200WithValidJson()
    {
        var client = BuildClient(HttpStatusCode.OK, """{"alerts":[{"id":"test-1"}]}""");

        var result = await client.GetAlertsAsync();

        result.Should().NotBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    public async Task GetAlertsAsync_ThrowsAlertApiException_WithStatusCode_On401Or403(
        HttpStatusCode statusCode, int expectedCode)
    {
        var client = BuildClient(statusCode, "Unauthorized");

        var act = async () => await client.GetAlertsAsync();

        var ex = await act.Should().ThrowAsync<AlertApiException>();
        ex.Which.StatusCode.Should().Be(expectedCode);
    }

    [Fact]
    public async Task GetAlertsAsync_ThrowsAlertApiException_WithZeroStatusCode_OnNonAuthHttpError()
    {
        var client = BuildClient(HttpStatusCode.InternalServerError, "error");

        var act = async () => await client.GetAlertsAsync();

        var ex = await act.Should().ThrowAsync<AlertApiException>();
        ex.Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task GetAlertsAsync_ThrowsAlertApiException_WithZeroStatusCode_OnNetworkError()
    {
        var httpClient = new HttpClient(new FailingHttpHandler())
        {
            BaseAddress = new Uri("https://app.xtrades.net")
        };
        var client = new AlertApiClient(httpClient);

        var act = async () => await client.GetAlertsAsync();

        var ex = await act.Should().ThrowAsync<AlertApiException>();
        ex.Which.StatusCode.Should().Be(0);
    }

    // -- CheckConnectionAsync --

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task CheckConnectionAsync_ReturnsTrue_OnSuccessOrNoContent(HttpStatusCode statusCode)
    {
        var client = BuildClient(statusCode);

        var result = await client.CheckConnectionAsync();

        result.Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    public async Task CheckConnectionAsync_ThrowsAlertApiException_WithStatusCode_On401Or403(
        HttpStatusCode statusCode, int expectedCode)
    {
        var client = BuildClient(statusCode);

        var act = async () => await client.CheckConnectionAsync();

        var ex = await act.Should().ThrowAsync<AlertApiException>();
        ex.Which.StatusCode.Should().Be(expectedCode);
    }

    [Fact]
    public async Task CheckConnectionAsync_ReturnsFalse_OnOtherNonSuccessStatus()
    {
        var client = BuildClient(HttpStatusCode.InternalServerError);

        var result = await client.CheckConnectionAsync();

        result.Should().BeFalse();
    }

    [Fact]
    public async Task CheckConnectionAsync_ReturnsFalse_OnNetworkError()
    {
        var httpClient = new HttpClient(new FailingHttpHandler())
        {
            BaseAddress = new Uri("https://app.xtrades.net")
        };
        var client = new AlertApiClient(httpClient);

        var result = await client.CheckConnectionAsync();

        result.Should().BeFalse();
    }

    // -- Helpers --

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public FakeHttpHandler(HttpStatusCode statusCode, string content = "")
        {
            _statusCode = statusCode;
            _content    = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content)
            });
    }

    private sealed class FailingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Simulated network failure");
    }
}