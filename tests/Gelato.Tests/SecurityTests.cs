using System.Reflection;
using Gelato.Controllers;
using Gelato.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Gelato.Tests;

public class SecurityTests
{
    [Theory]
    [InlineData(typeof(PalcoCacheController))]
    [InlineData(typeof(CatalogController))]
    public void GlobalAdministrationRequiresElevation(Type controller) =>
        Assert.Equal(Policies.RequiresElevation, controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy);

    [Fact]
    public void SubtitleEndpointRequiresAuthentication() =>
        Assert.NotNull(typeof(GelatoApiController).GetMethod("GetSubtitles")!.GetCustomAttribute<AuthorizeAttribute>());

    [Theory]
    [InlineData("false", false)]
    [InlineData("invalid", false)]
    [InlineData("true", true)]
    [InlineData(null, true)]
    public void RegistrationFlagFailsClosedOnInvalidData(string? value, bool enabled) =>
        Assert.Equal(enabled, RegistrationRequestLimiter.IsRegistrationEnabled(value));

    [Fact]
    public void AnonymousRegistrationHasPerClientLimits()
    {
        var limiter = new RegistrationRequestLimiter();
        for (var i = 0; i < 5; i++) Assert.True(limiter.TryAcquire("client"));
        Assert.False(limiter.TryAcquire("client"));
        Assert.True(limiter.TryAcquire("other-client"));
    }

    [Fact]
    public void TorrentTokenIsBoundToTheRequestedStream()
    {
        var access = new TorrentAccess(new EphemeralDataProtectionProvider());
        var token = access.Create("hash", 2, "trackers");
        Assert.True(access.Validate(token, "hash", 2, "trackers"));
        Assert.False(access.Validate(token, "other", 2, "trackers"));
        Assert.False(access.Validate(token, "hash", 3, "trackers"));
        Assert.False(access.Validate("bad", "hash", 2, "trackers"));
    }
}
