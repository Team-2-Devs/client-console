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
        
        await TestSubscriptions(token, async () =>
        {
            var uploadId = await ImageUploadStart(token);
            await ImageUploadConfirm(token, uploadId);
        });
    }

    private static async Task<string> ImageUploadStart(string token)
    {
        // Ask for filename
        Console.WriteLine("Enter filename: ");
        var filename = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(filename))
        {
            Console.WriteLine("Filename cannot be empty");
            return "";
        }

        var contentType = "image/png";

        Console.WriteLine("\n Starting image upload...");

        using var http = new HttpClient();

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        // Build GraphQL mutation payload to request a new image analysis
        var mutation = new
        {
            query = """
            mutation($filename: String!, $contentType: String!) {
            startUpload(filename: $filename, contentType: $contentType) {
                correlationId
                imageUploadPayload {
                    uploadId, key, putUrl, expiresAt
                }
            }
        }
        """,
            variables = new { filename, contentType }
        };

        // Send mutation via HTTP POST
        var response = await http.PostAsJsonAsync(GraphQLHttpUrl, mutation);
        var json = await response.Content.ReadAsStringAsync();

        // Fail fast if HTTP failed
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return "";
        }
        
        // Parse JSON and extract correlationId
        using var respDoc = JsonDocument.Parse(json);
        var root = respDoc.RootElement;

        // If GraphQL returned errors, show them and bail out
        if (root.TryGetProperty("errors", out var errorsElm) &&
            errorsElm.ValueKind == JsonValueKind.Array &&
            errorsElm.GetArrayLength() > 0)
        {
            Console.WriteLine("GraphQL errors:");
            Console.WriteLine(json);
            return "";
        }

        if (!root.TryGetProperty("data", out var dataElm) ||
            dataElm.ValueKind != JsonValueKind.Object ||
            !dataElm.TryGetProperty("startUpload", out var su) ||
            !su.TryGetProperty("correlationId", out var correlationIdElm) ||
            !su.TryGetProperty("imageUploadPayload", out var img) ||
            !img.TryGetProperty("uploadId", out var uploadIdElm) ||
            !img.TryGetProperty("key", out var keyElm) ||
            !img.TryGetProperty("putUrl", out var putUrlElm) ||
            !img.TryGetProperty("expiresAt", out var expiresAtElm))
        {
            Console.WriteLine("GraphQL response did not contain data.startUpload.imageUploadPayload.{uploadId,key,putUrl,expiresAt}");
            Console.WriteLine(json);
            return "";
        }

        var correlationId = correlationIdElm.GetString();
        var uploadId  = uploadIdElm.GetString();
        var key       = keyElm.GetString();
        var putUrl    = putUrlElm.GetString();
        var expiresAt = expiresAtElm.GetString();

        if (string.IsNullOrWhiteSpace(uploadId) ||
            string.IsNullOrWhiteSpace(key) ||
            string.IsNullOrWhiteSpace(putUrl))
        {
            Console.WriteLine("Missing required fields in startUpload response.");
            return "";
        }

        Console.WriteLine($"Got uploadId={uploadId}");
        Console.WriteLine($"Key={key}");
        Console.WriteLine($"PUT URL (presigned)={putUrl}");
        Console.WriteLine("Uploading file to object storage...");

        // 🔹 This is the equivalent of:
        // curl -X PUT -H "Content-Type: image/png" --data-binary @image.png "putUrl"

        if (!File.Exists(filename))
        {
            Console.WriteLine($"File not found: {filename}");
            return "";
        }

        // Use a separate HttpClient with NO Authorization header for S3/MinIO PUT
        using (var uploadClient = new HttpClient())
        {
            await using var fs = File.OpenRead(filename);
            using var content = new StreamContent(fs);

            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

            var putResponse = await uploadClient.PutAsync(putUrl, content);
            if (!putResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"Upload failed: HTTP {(int)putResponse.StatusCode} {putResponse.ReasonPhrase}");
                var body = await putResponse.Content.ReadAsStringAsync();
                Console.WriteLine(body);
                return "";
            }
        }

        Console.WriteLine("Upload succeeded.");

        return uploadId;
    }

    private static async Task<string> ImageUploadConfirm(string token, string uploadId)
    {
        var bytes = 1234;
        var checksum = "sha256:" + new string('a', 64);

        Console.WriteLine("\n Confirming upload...");

        using var http = new HttpClient();

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        // GraphQL mutation
        var mutation = new
        {
            query = """
            mutation($uploadId: String!, $bytes: Int!, $checksum: String!) {
            confirmUpload(uploadId: $uploadId, bytes: $bytes, checksum: $checksum) {
                status
            }
            }
            """,
            variables = new { uploadId, bytes, checksum }
        };

        var response = await http.PostAsJsonAsync(GraphQLHttpUrl, mutation);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return "";
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("errors", out var errorsElm) &&
            errorsElm.ValueKind == JsonValueKind.Array &&
            errorsElm.GetArrayLength() > 0)
        {
            Console.WriteLine("GraphQL errors:");
            Console.WriteLine(json);
            return "";
        }

        if (!root.TryGetProperty("data", out var dataElm) ||
            !dataElm.TryGetProperty("confirmUpload", out var cuElm) ||
            !cuElm.TryGetProperty("status", out var statusElm))
        {
            Console.WriteLine("GraphQL response missing data.confirmUpload.status");
            Console.WriteLine(json);
            return "";
        }

        var status = statusElm.GetString() ?? "";

        Console.WriteLine($"Upload confirmation status: {status}");

        return status;
    }

    private static async Task TestSubscriptions(string token, Func<Task> trigger)
    {
        using var http = new HttpClient();

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

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
                        success
                        recognitionPayload {
                            correlationId
                            objectKey
                            success
                        }
                    }
                }
                """
            }
        });

        Console.WriteLine($"Subscribed to analysis\n");
        _ = Task.Run(async () =>
        {
            try
            {
                await trigger();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TRIGGER ERROR] {ex}");
            }
        });

        
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
                    // if (started.GetProperty("correlationId").GetString() == correlationId)
                    Console.WriteLine($"[STARTED] {started}");
                }

                if (data.TryGetProperty("onAnalysisCompleted", out var completed))
                {
                    // if (completed.GetProperty("correlationId").GetString() == correlationId)
                    // {
                    Console.WriteLine($"[COMPLETED] {completed}");
                    break;
                    // }
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