using System;
using System.Threading.Tasks;
using Bogus;
using DaprApp;
using FluentAssertions.Extensions;
using Hypothesist;
using DaprApp.Controllers;
using MassTransit.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wrapr;
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

        using var logger = _output.BuildLogger();
        await using var host = await Host(hypothesis.ToHandler());
        await using var sidecar = await Sidecar(logger);
            
        // Act
        await Publish(message, logger);

        // Assert
        await hypothesis.Validate(10.Seconds());
    }
        
        
    private static async Task<Sidecar> Sidecar(ILogger logger)
    {
        var sidecar = new Sidecar("to-dapr", logger);
        await sidecar.Start(with => with
            .ComponentsPath("components")
            .AppPort(6000));

        return sidecar;
    }

    private async Task<IAsyncDisposable> Host(IHandler<int> handler)
    {
        var app = Startup.App(builder =>
        {
            builder.Services.AddSingleton(handler);
            builder.Logging.AddXunit(_output);
        });
        
        app.Urls.Add("http://localhost:6000");
        await app.StartAsync();
        
        return app;
    }

    private async Task Publish(UserLoggedIn message, ILogger logger)
    {
        LogContext.ConfigureCurrentLogContext(logger);
            
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