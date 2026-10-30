using Amazon;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.Runtime;
using FCG.Infrastructure.Persistence;
using FCG.Infrastructure.Persistence.Context;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.LocalStack;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FCG.IntegrationTests;

/// <summary>
/// Sobe a UsersAPI completa contra um PostgreSQL, um Redis e um LocalStack (SNS + SQS)
/// reais via Testcontainers. O pipeline de inicialização da API aplica as migrations e o
/// seed do admin, e o publisher de eventos (SNS) é apontado para o LocalStack.
/// </summary>
public class UsersApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TopicName = "fcg-user-events-test";
    private const string QueueName = "fcg-user-events-test-queue";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("fcgdb")
        .WithUsername("fcg")
        .WithPassword("fcg123")
        .Build();

    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:3")
        .WithEnvironment("SERVICES", "sns,sqs")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    private string _topicArn = string.Empty;
    private string _queueUrl = string.Empty;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _db.GetConnectionString(),
                ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
                ["Sns:TopicArn"] = _topicArn,
                ["Sns:ServiceUrl"] = _localStack.GetConnectionString()
            });
        });
    }

    /// <summary>Cliente SQS apontado para o LocalStack (para o teste consumir e validar a mensagem).</summary>
    public IAmazonSQS CreateSqsClient() =>
        new AmazonSQSClient(CreateLocalStackCredentials(), CreateLocalStackConfig<AmazonSQSConfig>());

    /// <summary>Fila (já inscrita no tópico) onde o teste deve consumir a mensagem publicada.</summary>
    public string QueueUrl => _queueUrl;

    private static BasicAWSCredentials CreateLocalStackCredentials() => new("test", "test");

    private T CreateLocalStackConfig<T>() where T : ClientConfig, new() => new()
    {
        ServiceURL = _localStack.GetConnectionString(),
        AuthenticationRegion = RegionEndpoint.USEast1.SystemName
    };

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_db.StartAsync(), _redis.StartAsync(), _localStack.StartAsync());

        // Credenciais fixas exigidas pelo SDK mesmo contra o LocalStack (que não valida
        // seu conteúdo, só exige que existam) — usadas tanto aqui quanto pelo cliente SNS
        // criado dentro da própria API via DI (ver Sns:ServiceUrl acima).
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "test");
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "test");
        Environment.SetEnvironmentVariable("AWS_REGION", "us-east-1");

        using var sns = new AmazonSimpleNotificationServiceClient(
            CreateLocalStackCredentials(), CreateLocalStackConfig<AmazonSimpleNotificationServiceConfig>());
        using var sqs = new AmazonSQSClient(
            CreateLocalStackCredentials(), CreateLocalStackConfig<AmazonSQSConfig>());

        var topic = await sns.CreateTopicAsync(new CreateTopicRequest { Name = TopicName });
        _topicArn = topic.TopicArn;

        var queue = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = QueueName });
        _queueUrl = queue.QueueUrl;

        var queueAttributes = await sqs.GetQueueAttributesAsync(
            new GetQueueAttributesRequest { QueueUrl = _queueUrl, AttributeNames = ["QueueArn"] });
        var queueArn = queueAttributes.QueueARN;

        var subscription = await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = _topicArn,
            Protocol = "sqs",
            Endpoint = queueArn
        });

        // RawMessageDelivery=true replica o comportamento de produção (ver template.yaml
        // do NotificationsAPI): sem isso, o corpo da mensagem SQS viria envelopado em JSON
        // do próprio SNS, e não o JSON puro do evento.
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subscription.SubscriptionArn,
            AttributeName = "RawMessageDelivery",
            AttributeValue = "true"
        });
    }

    /// <summary>
    /// Chamado antes de cada teste: deixa o banco só com o admin do seed e esvazia o
    /// Redis e a fila SQS, para nenhum teste enxergar estado deixado por outro.
    /// </summary>
    public async Task ResetAsync()
    {
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"Users\"");
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
        }

        await _redis.ExecAsync(["redis-cli", "FLUSHALL"]);

        // Drena a fila em vez de usar PurgeQueue, que só pode ser chamado a cada 60 s.
        using var sqs = CreateSqsClient();
        while (true)
        {
            var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 0
            });
            if (received.Messages is not { Count: > 0 }) break;

            foreach (var message in received.Messages)
                await sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle);
        }
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _db.DisposeAsync();
        await _redis.DisposeAsync();
        await _localStack.DisposeAsync();
        await base.DisposeAsync();
    }
}
