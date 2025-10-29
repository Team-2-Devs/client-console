using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using DotNetEnv;
using System.Net.Http.Headers;

class Program
{
    private static readonly string GraphQLHttpUrl = EnvVar("API_BASE");
    private static readonly string GraphQLWsUrl = EnvVar("WS_URL");

    public static async Task Main()
    {
        // Acquire token
        Env.Load();

        var tenantId = EnvVar("AZUREAD__TENANTID");
        var clientId = EnvVar("CLIENT_AZUREAD_CLIENTID");
        var scope = EnvVar("AUTH__REQUIREDSCOPE");

        var app = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .WithDefaultRedirectUri()
            .Build();

        var authResult = await app.AcquireTokenWithDeviceCode(new[] { scope }, dc =>
        {
            Console.WriteLine($"\nTo sign in, visit:\n{dc.VerificationUrl}\nCode: {dc.UserCode}\n");
            return Task.CompletedTask;
        }).ExecuteAsync();

        var token = authResult.AccessToken;

        // Ask for Object Key
        Console.WriteLine("Enter ObjectKey to analyze: ");
        var objectKey = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(objectKey))
        {
            Console.WriteLine("ObjectKey cannot be empty");
            return;
        }

        Console.WriteLine("\n Sending analysis request...");

        using var http = new HttpClient();

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        // Build GraphQL mutation payload to request a new image analysis
        var mutation = new
        {
            query = """
            mutation($key: String!) {
            requestAnalysis(input: {objectKey: $key }) {
                correlationId
            }
        }
        """,
            variables = new { key = objectKey }
        };

        // Open a WebSocket connection
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("graphql-transport-ws");
        ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        await ws.ConnectAsync(new Uri(GraphQLWsUrl), CancellationToken.None);

        // Send connection init
        await Send(ws, new { type = "connection_init", payload = new { } });
        await WaitForConnectionAck(ws);

        // Subscribe to started
        await Send(ws, new
        {
            id = "started",
            type = "subscribe",
            payload = new
            {
                query = """
                subscription {
                    onAnalysisStarted {
                        correlationId
                        objectKey
                    }
                }
                """,
            }
        });

        // Subscribe to completed
        await Send(ws, new
        {
            id = "completed",
            type = "subscribe",
            payload = new
            {
                query = """
                subscription {
                    onAnalysisCompleted {
                        correlationId
                        objectKey
                        success
                    }
                }
                """,
            }
        });

        // Send mutation via HTTP POST
        var response = await http.PostAsJsonAsync(GraphQLHttpUrl, mutation);
        var json = await response.Content.ReadAsStringAsync();

        // Fail fast if HTTP failed
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return;
        }

        // Parse JSON and extract correlationId
        using var respDoc = JsonDocument.Parse(json);
        if (!respDoc.RootElement.TryGetProperty("data", out var dataElm) ||
            !dataElm.TryGetProperty("requestAnalysis", out var ra) ||
            !ra.TryGetProperty("correlationId", out var cidElm))
        {
            Console.WriteLine("GraphQL response did not contain data.requestAnalysis.correlationId");
            Console.WriteLine(json);
            return;
        }
        var correlationId = cidElm.GetString();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            Console.WriteLine("Empty correlationId in response.");
            return;
        }

        Console.WriteLine($"Subscribed to analysis for {correlationId}\n");
        
        // Listen for events
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

            try
            {
                using var frameDoc = JsonDocument.Parse(message);
                var root = frameDoc.RootElement;

                if (!root.TryGetProperty("type", out var typeElm) || typeElm.GetString() != "next")
                    continue;

                if (!root.TryGetProperty("payload", out var payload) ||
                    !payload.TryGetProperty("data", out var data))
                    continue;

                if (data.TryGetProperty("onAnalysisStarted", out var started))
                {
                    if (started.GetProperty("correlationId").GetString() == correlationId)
                        Console.WriteLine($"[STARTED] {started}");
                }

                if (data.TryGetProperty("onAnalysisCompleted", out var completed))
                {
                    if (completed.GetProperty("correlationId").GetString() == correlationId)
                    {
                        Console.WriteLine($"[COMPLETED] {completed}");
                        break;
                    }
                }
            }
            catch
            {
                // Ignore, keep socket alive
            }
        }
        Console.WriteLine("Subscription closed.");
    }

    private static async Task WaitForConnectionAck(ClientWebSocket ws)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                Console.WriteLine($"Server closed during handshake: {ws.CloseStatus} {ws.CloseStatusDescription}");
                return;
            }

            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
            using var ackDoc = JsonDocument.Parse(msg);
            var root = ackDoc.RootElement;

            if (root.TryGetProperty("type", out var t))
            {
                var type = t.GetString();
                if (type == "ping")
                {
                    await Send(ws, new { type = "pong" });
                    continue;
                }
                if (type == "connection_ack")
                {
                    break; // safe to subscribe
                }
                if (type == "error")
                {
                    Console.WriteLine("Handshake error: " + msg);
                    return;
                }
            }
        }
    }

    private static Task Send(ClientWebSocket ws, object message)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        return ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    static string EnvVar(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing env var: {name}");
}