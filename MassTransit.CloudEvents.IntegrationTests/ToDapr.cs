using System;
using System.Threading.Tasks;
using Bogus;
using DaprApp;
using FluentAssertions.Extensions;
using Hypothesist;
using DaprApp.Controllers;
using Man.Dapr.Sidekick;
using Man.Dapr.Sidekick.Extensions.Logging;
using Man.Dapr.Sidekick.Threading;
using MassTransit.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace MassTransit.CloudEvents.IntegrationTests;

[Collection("user/loggedIn")]
public class ToDapr : IClassFixture<RabbitMqContainer>
{
    private readonly ITestOutputHelper _output;
    private readonly RabbitMqContainer _container;

    public ToDapr(ITestOutputHelper output, RabbitMqContainer container)
    {
        _output = output;
        _container = container;
    }

    [Fact]
    public async Task Do()
    {
        // Arrange
        var message = new Faker<UserLoggedIn>().CustomInstantiator(f => new UserLoggedIn(f.Random.Number())).Generate();
        var hypothesis = Hypothesis
            .For<int>()
            .Any(x => x == message.UserId);

        await using var host = await Host(hypothesis.ToHandler());
        await Sidecar(_output);
            
        // Act
        await Publish(message, _output);

        // Assert
        await hypothesis.Validate(10.Seconds());
    }
        
        
    private static async Task Sidecar(ITestOutputHelper logger)
    {
        var sidekick = new DaprSidekickBuilder().WithLoggerFactory(new DaprLoggerFactory(logger.ToLoggerFactory())).Build();
        sidekick.Sidecar.Start(() => new DaprOptions
        {
            Sidecar = new DaprSidecarOptions
            {
                ComponentsDirectory = "components",
                DaprGrpcPort = 3001
            }
        }, DaprCancellationToken.None);
        await Task.Delay(1.Seconds());
    }

    private async Task<IAsyncDisposable> Host(IHandler<int> handler)
    {
        var app = Startup.App(builder =>
        {
            builder.Services.AddSingleton(handler);
            builder.Logging.AddXUnit(_output);
        });
        
        app.Urls.Add("http://localhost:6000");
        await app.StartAsync();
        
        return app;
    }

    private async Task Publish(UserLoggedIn message, ITestOutputHelper logger)
    {
        LogContext.ConfigureCurrentLogContext(logger.ToLoggerFactory());
            
        var bus = Bus.Factory
            .CreateUsingRabbitMq(cfg =>
            {
                cfg.Host(_container.ConnectionString);
                cfg.UseCloudEvents()
                    .Type<UserLoggedIn>("loggedIn");
                    
                // set the topic/exchange
                cfg.Message<UserLoggedIn>(x => 
                    x.SetEntityName("user/loggedIn"));

                // if you don't want MassTransit to create the exchange
                cfg.PublishTopology.GetMessageTopology<UserLoggedIn>().Exclude = true;
            });

        await bus.StartAsync();
            
        var endpoint = await bus.GetPublishSendEndpoint<UserLoggedIn>();
        await endpoint.Send(message);
    }

    public record UserLoggedIn(int UserId);
}