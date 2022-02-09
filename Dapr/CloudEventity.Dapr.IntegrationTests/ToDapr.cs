using System;
using System.Threading.Tasks;
using DaprApp;
using DaprApp.Controllers;
using FluentAssertions.Extensions;
using Hypothesist;
using Man.Dapr.Sidekick;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace CloudEventity.Dapr.IntegrationTests;

public class ToDapr
{
    private readonly ITestOutputHelper _output;

    public ToDapr(ITestOutputHelper output) => 
        _output = output;

    [Fact]
    public async Task Do()
    {
        // Arrange
        var message = new UserLoggedIn(1234);
        var hypothesis = Hypothesis
            .For<int>()
            .Any(x => x == message.UserId);

        await using var host = await Host(hypothesis.ToHandler());
        await Task.Delay(1.Seconds()); // sidecar takes a while for being available

        // Act
        await Publish(message);

        // Assert
        await hypothesis.Validate(10.Seconds());
    }

    private async Task Publish(UserLoggedIn message)
    {
        var client = new DaprClient("http://localhost:3001");
        await client.PublishEvent("my-pubsub", "user/loggedIn", message);
    }

    private async Task<IAsyncDisposable> Host(IHandler<int> handler)
    {
        var app = Startup.App(builder =>
        {
            builder.Services
                .AddSingleton(handler)
                .AddDaprSidekick(configure => configure.Sidecar = new DaprSidecarOptions
                {
                    ComponentsDirectory = "components",
                    AppPort = 6000,
                    DaprGrpcPort = 3001
                });
            builder.Logging.AddXUnit();
        });
        
        app.Urls.Add("http://localhost:6000");
        await app.StartAsync();
        
        return app;
    }

    public record UserLoggedIn(int UserId);
}