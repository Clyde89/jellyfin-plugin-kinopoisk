#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Api;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests;

public sealed class KinopoiskEnhancedAvailabilityTests
{
    private static LocalPlugin Plugin(PluginStatus status, bool instance = false, bool supported = true, Guid? id = null)
        => new("fixture", supported, new PluginManifest { Id = id ?? KinopoiskEnhancedAvailability.EnhancedId,
            Name = "Jellyfin Enhanced", Version = "1.0.0.0", Status = status })
        { Instance = instance ? new Mock<IPlugin>().Object : null };

    [Fact]
    public void DetectedOnlyLoadedSupportedEnhancedByStableId()
    {
        IReadOnlyList<LocalPlugin> plugins = Array.Empty<LocalPlugin>();
        var manager = new Mock<IPluginManager>();
        manager.SetupGet(value => value.Plugins).Returns(() => plugins);
        var logger = new Mock<ILogger<KinopoiskEnhancedAvailability>>();
        var service = new KinopoiskEnhancedAvailability(manager.Object, logger.Object);
        Assert.Equal("missing", service.Read().State);
        plugins = new[] { Plugin(PluginStatus.Active, true, id: Guid.NewGuid()) };
        Assert.False(service.Read().Available);
        plugins = new[] { Plugin(PluginStatus.Disabled) };
        Assert.Equal("disabled", service.Read().State);
        plugins = new[] { Plugin(PluginStatus.Active) };
        Assert.Equal("pending", service.Read().State);
        plugins = new[] { Plugin(PluginStatus.Active, true, false) };
        Assert.False(service.Read().Available);
        plugins = new[] { Plugin(PluginStatus.Disabled), Plugin(PluginStatus.Active, true) };
        Assert.True(service.Read().Available);
        plugins = Array.Empty<LocalPlugin>();
        Assert.Equal("missing", service.Read().State);
    }

    [Fact]
    public void RepeatedReadsLoggedOnlyTransitionsAndRecoveredWithoutRecreatingService()
    {
        IReadOnlyList<LocalPlugin> plugins = Array.Empty<LocalPlugin>();
        var manager = new Mock<IPluginManager>();
        manager.SetupGet(value => value.Plugins).Returns(() => plugins);
        var logger = new Mock<ILogger<KinopoiskEnhancedAvailability>>();
        var service = new KinopoiskEnhancedAvailability(manager.Object, logger.Object);
        for (var index = 0; index < 50; index++) Assert.False(service.Read().Available);
        Assert.Single(logger.Invocations.Where(call => call.Method.Name == "Log"));
        plugins = new[] { Plugin(PluginStatus.Active, true) };
        Assert.True(service.Read().Available);
        Assert.True(service.Read().Available);
        Assert.Equal(2, logger.Invocations.Count(call => call.Method.Name == "Log"));
    }

    [Fact]
    public void RegistryFailureClosedIntegrationWithoutExposingException()
    {
        var manager = new Mock<IPluginManager>();
        manager.SetupGet(value => value.Plugins).Throws(new InvalidOperationException("sensitive-fixture"));
        var logger = new Mock<ILogger<KinopoiskEnhancedAvailability>>();
        var service = new KinopoiskEnhancedAvailability(manager.Object, logger.Object);
        Assert.Equal("unknown", service.Read().State);
        Assert.False(service.Read().Available);
        var call = Assert.Single(logger.Invocations.Where(value => value.Method.Name == "Log"));
        Assert.DoesNotContain("sensitive-fixture", string.Join(" ", call.Arguments.Select(value => value?.ToString())));
    }

    [Fact]
    public async Task AvailabilityRouteRequiredAuthenticationAndAllowedOrdinaryUserWithoutExposingInventory()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var manager = new Mock<IPluginManager>();
        manager.SetupGet(value => value.Plugins).Returns(new[] { Plugin(PluginStatus.Active, true) });
        builder.Services.AddSingleton(manager.Object);
        builder.Services.AddSingleton<KinopoiskEnhancedAvailability>();
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, IdentityHandler>("fixture", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().AddApplicationPart(typeof(KinopoiskIntegrationsController).Assembly);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/KinopoiskRequests/integrations")).StatusCode);
            http.DefaultRequestHeaders.Add("fixture-user", "ordinary");
            using var response = await http.GetAsync("/KinopoiskRequests/integrations");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal("{\"enhanced\":{\"available\":true,\"state\":\"available\"}}", body);
            Assert.DoesNotContain(KinopoiskEnhancedAvailability.EnhancedId.ToString(), body);
        }
        finally { await app.StopAsync(); }
    }

    public sealed class IdentityHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public IdentityHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Request.Headers.ContainsKey("fixture-user")
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("Jellyfin-UserId", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") }, Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }
}
