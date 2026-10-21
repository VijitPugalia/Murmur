using Murmur.Core.Http;
using Xunit;

namespace Murmur.Tests;

public class HttpErrorProviderParsingTests
{
    [Fact]
    public void TryExtractProviderErrorMessage_NestedJsonError_ExtractsMessage()
    {
        var json = "{\"error\": {\"message\": \"could not process audio file\", \"type\": \"invalid_request_error\"}}";
        var result = HttpError.TryExtractProviderErrorMessage(json);
        Assert.Equal("could not process audio file", result);
    }

    [Fact]
    public void TryExtractProviderErrorMessage_StringJsonError_ExtractsMessage()
    {
        var json = "{\"error\": \"Invalid model specified\"}";
        var result = HttpError.TryExtractProviderErrorMessage(json);
        Assert.Equal("Invalid model specified", result);
    }

    [Fact]
    public void TryExtractProviderErrorMessage_DirectMessageProperty_ExtractsMessage()
    {
        var json = "{\"message\": \"Rate limit reached\"}";
        var result = HttpError.TryExtractProviderErrorMessage(json);
        Assert.Equal("Rate limit reached", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not JSON at all")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("{}")]
    public void TryExtractProviderErrorMessage_InvalidOrEmpty_ReturnsNull(string? responseBody)
    {
        var result = HttpError.TryExtractProviderErrorMessage(responseBody);
        Assert.Null(result);
    }

    [Fact]
    public void FromStatusCode_400WithProviderMessage_IncludesProviderReason()
    {
        var json = "{\"error\": {\"message\": \"Audio file is too short (0.05s)\"}}";
        var error = HttpError.FromStatusCode(400, "api.groq.com", json);

        Assert.Equal(HttpErrorType.BadRequest, error.Type);
        Assert.Equal("api.groq.com rejected the request (HTTP 400): Audio file is too short (0.05s)", error.Message);
        Assert.Equal(json, error.ResponseBody);
    }

    [Fact]
    public void FromStatusCode_400WithoutResponseBody_UsesDefaultFriendlyMessage()
    {
        var error = HttpError.FromStatusCode(400, "api.groq.com", null);

        Assert.Equal(HttpErrorType.BadRequest, error.Type);
        Assert.Equal("Provider rejected the request (HTTP 400). Check your model name and Base URL in Settings.", error.Message);
    }
}
