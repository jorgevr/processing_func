using Azure.Messaging.ServiceBus;

const string ConnectionString =
    "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;" +
    "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

const string QueueName = "raw-energy-events";

// Usage:
//   dotnet run                  — send the test message and peek the DLQ
//   dotnet run -- dlq           — peek the DLQ without sending
//   dotnet run -- requeue       — move all DLQ messages back to the main queue
var command = args.FirstOrDefault()?.ToLowerInvariant();

await using var client = new ServiceBusClient(ConnectionString);

if (command == "requeue")
{
    await RequeueDlqAsync(client);
    return;
}

if (command == "dlq")
{
    await PeekDlqAsync(client);
    return;
}

// Default: send a test message then peek DLQ
// Matches the CloudEvents envelope published by the ingestion-func.
// Edit site_id / category / storage_path to match a file that exists in your local Azurite bronze container.
var payload = """
{
  "specversion": "1.0",
  "type": "solar.pvdaq.dataset.available",
  "source": "/energy-ingestion-boundary/pvdaq",
  "id": "2e57b15a-0d1e-48e3-b5b1-25f373ca9e8a",
  "time": "2026-04-09T21:24:06.059870+00:00",
  "datacontenttype": "application/json",
  "tenant_id": "research",
  "source_vendor": "PVDAQ",
  "schema_version": "v1",
  "mapping_version": "unknown",
  "correlation_id": "ce498e38-f99c-4cb1-9824-9c37b2f3f0cd",
  "ingestion_timestamp": "2026-04-09T21:24:06.059870+00:00",
  "traceparent": "00-99df39bf690948f8b6820dca9ca43c09-c2163a2631c349d5-01",
  "data": {
    "site_id": 9068,
    "category": "ac_power_data_20240101_20250430",
    "file_format": "csv",
    "storage_path": "http://127.0.0.1:10000/devstoreaccount1/bronze/source=pvdaq/dataset=9068_ac_power_data_20240101_20250430/ingestion_date=2026-04-09/9068_ac_power_data_20240101_20250430_v1.csv",
    "version": 1,
    "ingestion_id": "ce498e38-f99c-4cb1-9824-9c37b2f3f0cd",
    "source_url": "https://oedi-data-lake.s3.amazonaws.com/pvdaq/2023-solar-data-prize/9068_OEDI/data/9068_ac_power_data_20240101_20250430.csv",
    "file_size": 10870311,
    "file_hash": "16cac9d3ec61027e6f176eea276dafd1ad6d46461336edb29a3772ad34b5efc2"
  }
}
""";

await using var sender = client.CreateSender(QueueName);
var message = new ServiceBusMessage(payload)
{
    ContentType = "application/cloudevents+json",
    Subject = "solar.pvdaq.dataset.available",
    MessageId = Guid.NewGuid().ToString()
};

await sender.SendMessageAsync(message);
Console.WriteLine($"Sent to '{QueueName}'. Waiting 5s for processing...");
await Task.Delay(5000);

await PeekDlqAsync(client);

// ---------------------------------------------------------------------------

static async Task PeekDlqAsync(ServiceBusClient client)
{
    await using var receiver = client.CreateReceiver(
        QueueName, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

    var messages = await receiver.PeekMessagesAsync(maxMessages: 100);
    if (messages.Count == 0)
    {
        Console.WriteLine("No messages in DLQ.");
        return;
    }

    Console.WriteLine($"\n{messages.Count} message(s) in DLQ:");
    foreach (var m in messages)
    {
        Console.WriteLine($"  [{m.SequenceNumber}] Reason     : {m.DeadLetterReason}");
        Console.WriteLine($"           Description: {m.DeadLetterErrorDescription}");
        Console.WriteLine($"           MessageId  : {m.MessageId}");
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(m.Body);
            var data = doc.RootElement.GetProperty("data");
            Console.WriteLine($"           StoragePath: {data.GetProperty("storage_path").GetString()}");
        }
        catch
        {
            Console.WriteLine($"           Body       : {m.Body}");
        }
        Console.WriteLine();
    }
}

static async Task RequeueDlqAsync(ServiceBusClient client)
{
    await using var dlqReceiver = client.CreateReceiver(
        QueueName, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
    await using var sender = client.CreateSender(QueueName);

    var requeued = 0;
    while (true)
    {
        var batch = await dlqReceiver.ReceiveMessagesAsync(
            maxMessages: 10, maxWaitTime: TimeSpan.FromSeconds(3));
        if (batch.Count == 0) break;

        foreach (var dlqMsg in batch)
        {
            var fresh = new ServiceBusMessage(dlqMsg.Body)
            {
                ContentType   = dlqMsg.ContentType,
                Subject       = dlqMsg.Subject,
                MessageId     = dlqMsg.MessageId,
                CorrelationId = dlqMsg.CorrelationId,
            };
            await sender.SendMessageAsync(fresh);
            await dlqReceiver.CompleteMessageAsync(dlqMsg);
            Console.WriteLine($"Requeued [{dlqMsg.SequenceNumber}] {dlqMsg.MessageId}");
            requeued++;
        }
    }

    Console.WriteLine($"\nDone. {requeued} message(s) moved back to '{QueueName}'.");
}
