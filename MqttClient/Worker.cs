using MQTTnet;
using MQTTnet.Client;
using SpanJson;

namespace MqttClient;

public class Worker : IHostedService
{
    private readonly ILogger<Worker> _logger;
    private readonly MqttClientConfiguration _mqttConfig;
    private IMqttClient _mqttClient;

    public Worker(
        ILogger<Worker> logger,
        MqttClientConfiguration mqttConfig)
    {
        _logger = logger;
        _mqttConfig = mqttConfig;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("{Name} started", nameof(Worker));

        var mqttFactory = new MqttFactory();
        _mqttClient = mqttFactory.CreateMqttClient();
        
        var mqttClientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_mqttConfig.Address, _mqttConfig.Port)
            .WithCredentials(_mqttConfig.Username, _mqttConfig.Password)
            .Build();
        
        var mqttSubscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(_mqttConfig.Topic))
            .Build();

        AddMessageHandler();
        await Connect(mqttClientOptions, mqttSubscribeOptions, ct);
        StartReconnectLoop(mqttClientOptions, mqttSubscribeOptions, ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Disconnecting MQTT client...");
            await _mqttClient.DisconnectAsync(cancellationToken: ct);
            _mqttClient.Dispose();
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("{Name} cancelled", nameof(Worker));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while disconnecting MQTT client");
        }

        _logger.LogInformation("{Name} stopped", nameof(Worker));
    }
    
    private void AddMessageHandler()
    {
        _mqttClient.ApplicationMessageReceivedAsync += e =>
        {
            try
            {
                string json = e.ApplicationMessage.ConvertPayloadToString();
                string[] commands = JsonSerializer.Generic.Utf16.Deserialize<string[]>(json);
            
                _logger.LogDebug("Received message: {Message}", string.Join(", ", commands));

                // TODO: run all commands
                // TODO: use ControlMyMonitor (path env var?)
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while processing message");
            }

            return Task.CompletedTask;
        };
    }

    private void StartReconnectLoop(
        MqttClientOptions mqttClientOptions,
        MqttClientSubscribeOptions mqttClientSubscribeOptions, 
        CancellationToken ct)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                    try
                    {
                        if (await _mqttClient.TryPingAsync(ct))
                        {
                            _logger.LogDebug("MQTT client keeps the connection");
                        }
                        else
                        {
                            _logger.LogWarning("MQTT client not connected, trying to reconnect...");
                            await Connect(mqttClientOptions, mqttClientSubscribeOptions, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error while connecting");
                    }
                    finally
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_mqttConfig.ReconnectDelaySeconds), ct);
                    }
            }, ct);
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("{Name} cancelled", nameof(Worker));
        }
    }

    private async Task Connect(
        MqttClientOptions mqttClientOptions,
        MqttClientSubscribeOptions mqttClientSubscribeOptions, 
        CancellationToken ct)
    {
        try
        {
            var response = await _mqttClient.ConnectAsync(mqttClientOptions, ct);

            if (response.ResultCode != MqttClientConnectResultCode.Success)
                _logger.LogWarning("Connection status: {Status}", response.ResultCode);
            
            _logger.LogInformation("Connected: {@Response}", response);
            await SubscribeToTopic(mqttClientSubscribeOptions, ct);
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("{Name} cancelled", nameof(Worker));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while connecting");
        }
    }

    private async Task SubscribeToTopic(MqttClientSubscribeOptions mqttClientSubscribeOptions, CancellationToken ct)
    {
        try
        {
            var response = await _mqttClient.SubscribeAsync(mqttClientSubscribeOptions, ct);
            
            _logger.LogInformation("Connected to a topic {Topic} with response: {@Response}",
                _mqttConfig.Topic, response);
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("{Name} cancelled", nameof(Worker));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT client can't subscribe to a topic {Topic}", _mqttConfig.Topic);
        }
    }
}