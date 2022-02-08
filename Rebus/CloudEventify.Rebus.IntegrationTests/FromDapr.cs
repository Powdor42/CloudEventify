using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Bogus;
using CloudNative.CloudEvents;
using Dapr.Client;
using Dapr.Client.Autogen.Grpc.v1;
using DaprApp;
using FluentAssertions.Extensions;
using Google.Protobuf;
using Grpc.Net.Client;
using Hypothesist;
using Hypothesist.Rebus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Rebus.Activation;
using Rebus.Config;
using Wrapr;
using Xunit;
using Xunit.Abstractions;

namespace CloudEventify.Rebus.IntegrationTests;

[Collection("user/loggedIn")]
public class FromDapr : IClassFixture<RabbitMqContainer>
{
    private readonly ITestOutputHelper _output;
    private readonly RabbitMqContainer _container;

    public FromDapr(ITestOutputHelper output, RabbitMqContainer container)
    {
        _output = output;
        _container = container;
    }

    [Fact]
    public async Task Do()
    {
        // Arrange
        const string topic = "user/loggedIn";
        const string queue = "user:loggedIn:rabbitmq";
        
        var message = Message();
        var hypothesis = Hypothesis
            .For<UserLoggedIn>()
            .Any(x => x == message);

        using var logger = _output.BuildLogger();
        var activator = new BuiltinHandlerActivator()
            .Register(hypothesis.AsHandler);
        var subscriber = Configure.With(activator)
            .Transport(t => t.UseRabbitMq(_container.ConnectionString, queue))
            .Serialization(s => s.UseCloudEvents())
            .Logging(l => l.MicrosoftExtensionsLogging(logger))
            .Start();
        await subscriber.Subscribe<UserLoggedIn>();

        RouteToRebus(_container.ConnectionString, topic, queue);
        
        // Act
        await Publish(topic, message, logger);

        // Assert
        await hypothesis.Validate(5.Seconds());
    }
    
    private static void RouteToRebus(string connectionString, string topic, string queue)
    {
        var model = new ConnectionFactory
            {
                Endpoint = new AmqpTcpEndpoint(new Uri(connectionString))
            }
            .CreateConnection()
            .CreateModel();
        
        model.ExchangeDeclare(topic, "fanout", durable: true);
        model.QueueBind(queue, topic, "");
    }

    private static async Task Publish(string topic, UserLoggedIn message, ILogger logger)
    {
        await using var sidecar = new Sidecar("from-dapr-to-rabbitmq", logger);
        await sidecar.Start(with => with
            .ComponentsPath("components")
            .DaprGrpcPort(3001));

        // using var client = new DaprClientBuilder()
        //     .UseGrpcEndpoint("http://localhost:3001")
        //     .Build();
        // await client.PublishEventAsync("my-pubsub", "user/loggedIn", message);
        
        // var client = new HttpClient();
        // var content = new ByteArrayContent(envelope);
        // content.Headers.ContentType = new MediaTypeHeaderValue("application/cloudevents+json");
        //
        // var response = await client.PostAsync("http://localhost:3002/v1.0/publish/my-pubsub/user/loggedIn", content);
        // logger.LogInformation(await response.Content.ReadAsStringAsync());
        // response.EnsureSuccessStatusCode();
        
        await new DaprClient("http://localhost:3001")
            .PublishEvent("my-pubsub", topic, message);
    }

    public record UserLoggedIn(string Id);
        
    private static UserLoggedIn Message() => 
        new Faker<UserLoggedIn>()
            .CustomInstantiator(f => new UserLoggedIn(f.Random.Hash()))
            .Generate();
}