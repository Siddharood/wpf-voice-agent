using System;
using System.Collections.Generic;
using System.Configuration;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WpfVoiceAgent.OpenAI
{
    public sealed class OpenAiRealtimeClient : IDisposable
    {
        private readonly string _apiKey;
        private readonly string _model;

        private readonly object _connectionLock =
            new object();

        private readonly SemaphoreSlim _sendLock =
            new SemaphoreSlim(1, 1);

        private ClientWebSocket _socket;
        private CancellationTokenSource _receiveCts;

        /*
         * Every physical WebSocket connection gets a generation.
         *
         * If an old receive loop finishes after a new connection
         * has already been created, its Disconnected event is ignored.
         */
        private int _connectionGeneration;

        private bool _disposed;

        public event Action<string> Status;

        /*
         * responseId + PCM audio.
         *
         * The controller uses the response ID to reject audio
         * belonging to an older response.
         */
        public event Action<string, byte[]> AudioReceived;

        public event Action<string> TranscriptDelta;

        public event Action<string> UserTranscript;

        /*
         * A Realtime response has been created.
         */
        public event Action<string> ResponseStarted;

        /*
         * response.done
         */
        public event Action<string> ResponseCompleted;

        /*
         * response.output_audio.done
         */
        public event Action<string> OutputAudioCompleted;

        public event Action Disconnected;

        public bool IsConnected
        {
            get
            {
                lock (_connectionLock)
                {
                    return _socket != null &&
                           _socket.State == WebSocketState.Open;
                }
            }
        }

        public OpenAiRealtimeClient()
        {
            string apiKey =
                ConfigurationManager
                    .AppSettings["OpenAIApiKey"];

            string model =
                ConfigurationManager
                    .AppSettings["OpenAIRealtimeModel"];

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException(
                    "OpenAI API key is missing.",
                    nameof(apiKey));
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                throw new ArgumentException(
                    "Realtime model is missing.",
                    nameof(model));
            }

            _apiKey = apiKey;
            _model = model;
        }

        // =========================================================
        // CONNECT
        // =========================================================

        public async Task ConnectAsync(
            CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(OpenAiRealtimeClient));
            }

            if (IsConnected)
            {
                Status?.Invoke(
                    "OpenAI Realtime already connected.");

                return;
            }

            /*
             * Invalidate any previous connection first.
             */
            CancellationTokenSource oldReceiveCts;
            ClientWebSocket oldSocket;

            lock (_connectionLock)
            {
                _connectionGeneration++;

                oldReceiveCts = _receiveCts;
                oldSocket = _socket;

                _receiveCts = null;
                _socket = null;
            }

            if (oldReceiveCts != null)
            {
                try
                {
                    oldReceiveCts.Cancel();
                }
                catch
                {
                }

                try
                {
                    oldReceiveCts.Dispose();
                }
                catch
                {
                }
            }

            if (oldSocket != null)
            {
                try
                {
                    oldSocket.Dispose();
                }
                catch
                {
                }
            }

            ClientWebSocket socket =
                new ClientWebSocket();

            socket.Options.SetRequestHeader(
                "Authorization",
                "Bearer " + _apiKey);

            string url =
                "wss://api.openai.com/v1/realtime?model=" +
                Uri.EscapeDataString(_model);

            Status?.Invoke(
                "Connecting to OpenAI Realtime...");

            await socket.ConnectAsync(
                new Uri(url),
                cancellationToken);

            if (_disposed)
            {
                try
                {
                    socket.Dispose();
                }
                catch
                {
                }

                throw new ObjectDisposedException(
                    nameof(OpenAiRealtimeClient));
            }

            CancellationTokenSource receiveCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            int generation;

            lock (_connectionLock)
            {
                generation =
                    _connectionGeneration;

                _socket = socket;
                _receiveCts = receiveCts;
            }

            Status?.Invoke(
                "OpenAI Realtime connected.");

            /*
             * Start receive loop using the exact socket and
             * generation that belong to this connection.
             */
            _ = Task.Run(
                () => ReceiveLoopAsync(
                    socket,
                    receiveCts,
                    generation));

            /*
             * IMPORTANT:
             *
             * This completes before BeginRealtimeTurnAsync()
             * allows microphone audio to be sent.
             *
             * Therefore audio cannot race session.update.
             */
            await SendSessionUpdateAsync(
                cancellationToken);
        }

        // =========================================================
        // SESSION CONFIGURATION
        // =========================================================

        private async Task SendSessionUpdateAsync(
            CancellationToken cancellationToken)
        {
            var session =
                new Dictionary<string, object>
                {
                    ["type"] =
                        "session.update",

                    ["session"] =
                        new Dictionary<string, object>
                        {
                            ["type"] =
                                "realtime",

                            ["instructions"] =
                                "You are a helpful voice assistant. " +
                                "Answer the user naturally and concisely. " +
                                "The user is speaking to you through a desktop " +
                                "microphone and listening through speakers.",

                            ["output_modalities"] =
                                new[] { "audio" },

                            ["audio"] =
                                new Dictionary<string, object>
                                {
                                    ["input"] =
                                        new Dictionary<string, object>
                                        {
                                            ["format"] =
                                                new Dictionary<string, object>
                                                {
                                                    ["type"] =
                                                        "audio/pcm",

                                                    ["rate"] =
                                                        24000
                                                },

                                            ["transcription"] =
                                                new Dictionary<string, object>
                                                {
                                                    ["model"] =
                                                        "gpt-transcribe",

                                                    ["language"] =
                                                        "en"
                                                },

                                            ["turn_detection"] =
                                                new Dictionary<string, object>
                                                {
                                                    ["type"] =
                                                        "server_vad",

                                                    ["threshold"] =
                                                        0.5,

                                                    ["prefix_padding_ms"] =
                                                        300,

                                                    ["silence_duration_ms"] =
                                                        700,

                                                    ["create_response"] =
                                                        true,

                                                    ["interrupt_response"] =
                                                        true
                                                }
                                        },

                                    ["output"] =
                                        new Dictionary<string, object>
                                        {
                                            ["format"] =
                                                new Dictionary<string, object>
                                                {
                                                    ["type"] =
                                                        "audio/pcm",

                                                    ["rate"] =
                                                        24000
                                                }
                                        }
                                }
                        }
                };

            string json =
                JsonConvert.SerializeObject(
                    session);

            await SendJsonAsync(
                json,
                cancellationToken);

            Status?.Invoke(
                "Realtime session configuration sent.");
        }

        // =========================================================
        // SEND AUDIO
        // =========================================================

        public async Task SendAudioAsync(
            byte[] pcm24k,
            CancellationToken cancellationToken)
        {
            if (pcm24k == null ||
                pcm24k.Length == 0)
            {
                return;
            }

            if (!IsConnected)
            {
                return;
            }

            string audioBase64 =
                Convert.ToBase64String(
                    pcm24k);

            var message =
                new Dictionary<string, object>
                {
                    ["type"] =
                        "input_audio_buffer.append",

                    ["audio"] =
                        audioBase64
                };

            string json =
                JsonConvert.SerializeObject(
                    message);

            await SendJsonAsync(
                json,
                cancellationToken);
        }

        // =========================================================
        // CANCEL RESPONSE
        // =========================================================

        public async Task CancelResponseAsync(
            CancellationToken cancellationToken)
        {
            if (!IsConnected)
            {
                return;
            }

            var message =
                new Dictionary<string, object>
                {
                    ["type"] =
                        "response.cancel"
                };

            string json =
                JsonConvert.SerializeObject(
                    message);

            await SendJsonAsync(
                json,
                cancellationToken);
        }

        // =========================================================
        // SEND JSON
        // =========================================================

        private async Task SendJsonAsync(
            string json,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            byte[] bytes =
                Encoding.UTF8.GetBytes(
                    json);

            await _sendLock.WaitAsync(
                cancellationToken);

            try
            {
                ClientWebSocket socket;

                lock (_connectionLock)
                {
                    socket = _socket;
                }

                if (socket == null ||
                    socket.State != WebSocketState.Open)
                {
                    return;
                }

                await socket.SendAsync(
                    new ArraySegment<byte>(
                        bytes),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // =========================================================
        // RECEIVE LOOP
        // =========================================================

        private async Task ReceiveLoopAsync(
            ClientWebSocket socket,
            CancellationTokenSource receiveCts,
            int generation)
        {
            byte[] buffer =
                new byte[8192];

            try
            {
                while (!receiveCts.Token.IsCancellationRequested &&
                       socket.State == WebSocketState.Open)
                {
                    var messageBuilder =
                        new StringBuilder();

                    WebSocketReceiveResult result;

                    do
                    {
                        result =
                            await socket.ReceiveAsync(
                                new ArraySegment<byte>(
                                    buffer),
                                receiveCts.Token);

                        if (result.MessageType ==
                            WebSocketMessageType.Close)
                        {
                            Status?.Invoke(
                                "Realtime server requested WebSocket close.");

                            return;
                        }

                        if (result.MessageType !=
                            WebSocketMessageType.Text)
                        {
                            continue;
                        }

                        messageBuilder.Append(
                            Encoding.UTF8.GetString(
                                buffer,
                                0,
                                result.Count));

                    }
                    while (!result.EndOfMessage);

                    string json =
                        messageBuilder.ToString();

                    if (string.IsNullOrWhiteSpace(
                        json))
                    {
                        continue;
                    }

                    ProcessServerMessage(
                        json);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException ex)
            {
                Status?.Invoke(
                    "Realtime WebSocket error: " +
                    ex.Message);
            }
            catch (Exception ex)
            {
                Status?.Invoke(
                    "Realtime receive error: " +
                    ex.Message);
            }
            finally
            {
                bool currentConnection;

                lock (_connectionLock)
                {
                    currentConnection =
                        generation == _connectionGeneration &&
                        ReferenceEquals(
                            _socket,
                            socket);

                    if (currentConnection)
                    {
                        _socket = null;

                        if (ReferenceEquals(
                            _receiveCts,
                            receiveCts))
                        {
                            _receiveCts = null;
                        }
                    }
                }

                /*
                 * An old receive loop must never disconnect a
                 * newer connection.
                 */
                if (currentConnection)
                {
                    Disconnected?.Invoke();
                }
            }
        }

        // =========================================================
        // PROCESS SERVER EVENTS
        // =========================================================

        private void ProcessServerMessage(
            string json)
        {
            try
            {
                JObject root =
                    JObject.Parse(
                        json);

                string type =
                    root["type"]?.Value<string>();

                if (string.IsNullOrWhiteSpace(
                    type))
                {
                    return;
                }

                switch (type)
                {
                    case "session.created":
                        Status?.Invoke(
                            "Realtime session created.");
                        break;

                    case "session.updated":
                        Status?.Invoke(
                            "Realtime session updated.");
                        break;

                    case "input_audio_buffer.speech_started":
                        Status?.Invoke(
                            "User speech detected.");
                        break;

                    case "input_audio_buffer.speech_stopped":
                        Status?.Invoke(
                            "User speech ended.");
                        break;

                    case "conversation.item.input_audio_transcription.completed":
                        {
                            string transcript =
                                root["transcript"]
                                    ?.Value<string>();

                            if (!string.IsNullOrWhiteSpace(
                                transcript))
                            {
                                UserTranscript?.Invoke(
                                    transcript);
                            }

                            break;
                        }

                    // =================================================
                    // RESPONSE CREATED
                    // =================================================

                    case "response.created":
                        {
                            string responseId =
                                root["response"]?["id"]
                                    ?.Value<string>();

                            if (!string.IsNullOrWhiteSpace(
                                responseId))
                            {
                                Status?.Invoke(
                                    "Realtime response started. " +
                                    "ResponseId=" +
                                    responseId);

                                ResponseStarted?.Invoke(
                                    responseId);
                            }

                            break;
                        }

                    // =================================================
                    // AUDIO DELTA
                    // =================================================

                    case "response.output_audio.delta":
                        {
                            string responseId =
                                root["response_id"]
                                    ?.Value<string>();

                            string delta =
                                root["delta"]
                                    ?.Value<string>();

                            if (!string.IsNullOrWhiteSpace(
                                responseId) &&
                                !string.IsNullOrWhiteSpace(
                                    delta))
                            {
                                try
                                {
                                    byte[] pcm =
                                        Convert.FromBase64String(
                                            delta);

                                    AudioReceived?.Invoke(
                                        responseId,
                                        pcm);
                                }
                                catch (FormatException)
                                {
                                    Status?.Invoke(
                                        "Invalid Realtime audio delta.");
                                }
                            }

                            break;
                        }

                    // =================================================
                    // AUDIO COMPLETE
                    // =================================================

                    case "response.output_audio.done":
                        {
                            string responseId =
                                root["response_id"]
                                    ?.Value<string>();

                            if (!string.IsNullOrWhiteSpace(
                                responseId))
                            {
                                Status?.Invoke(
                                    "Realtime audio response completed. " +
                                    "ResponseId=" +
                                    responseId);

                                OutputAudioCompleted?.Invoke(
                                    responseId);
                            }

                            break;
                        }

                    // =================================================
                    // AUDIO TRANSCRIPT
                    // =================================================

                    case "response.output_audio_transcript.delta":
                        {
                            string delta =
                                root["delta"]
                                    ?.Value<string>();

                            if (!string.IsNullOrEmpty(
                                delta))
                            {
                                TranscriptDelta?.Invoke(
                                    delta);
                            }

                            break;
                        }

                    // =================================================
                    // RESPONSE DONE
                    // =================================================

                    case "response.done":
                        {
                            string responseId =
                                root["response"]?["id"]
                                    ?.Value<string>();

                            string responseStatus =
                                root["response"]?["status"]
                                    ?.Value<string>();

                            Status?.Invoke(
                                "Realtime response completed. " +
                                "Status=" +
                                (string.IsNullOrWhiteSpace(
                                    responseStatus)
                                    ? "unknown"
                                    : responseStatus) +
                                " ResponseId=" +
                                (string.IsNullOrWhiteSpace(
                                    responseId)
                                    ? "unknown"
                                    : responseId));

                            if (!string.IsNullOrWhiteSpace(
                                responseId))
                            {
                                ResponseCompleted?.Invoke(
                                    responseId);
                            }

                            break;
                        }

                    // =================================================
                    // ERROR
                    // =================================================

                    case "error":
                        {
                            string message =
                                root["error"]?["message"]
                                    ?.Value<string>();

                            string errorType =
                                root["error"]?["type"]
                                    ?.Value<string>();

                            string errorCode =
                                root["error"]?["code"]
                                    ?.Value<string>();

                            Status?.Invoke(
                                "OpenAI Realtime error: " +
                                (!string.IsNullOrWhiteSpace(
                                    message)
                                    ? message
                                    : "Unknown error") +
                                (!string.IsNullOrWhiteSpace(
                                    errorType)
                                    ? " [" +
                                      errorType +
                                      "]"
                                    : "") +
                                (!string.IsNullOrWhiteSpace(
                                    errorCode)
                                    ? " Code=" +
                                      errorCode
                                    : ""));

                            break;
                        }

                    default:
                        Status?.Invoke(
                            "Realtime event: " +
                            type);
                        break;
                }
            }
            catch (Exception ex)
            {
                Status?.Invoke(
                    "Realtime message processing error: " +
                    ex.Message);
            }
        }

        // =========================================================
        // DISCONNECT
        // =========================================================

        public async Task DisconnectAsync()
        {
            CancellationTokenSource receiveCts;
            ClientWebSocket socket;

            lock (_connectionLock)
            {
                /*
                 * Invalidate the current receive loop immediately.
                 */
                _connectionGeneration++;

                receiveCts =
                    _receiveCts;

                socket =
                    _socket;

                _receiveCts = null;
                _socket = null;
            }

            if (receiveCts != null)
            {
                try
                {
                    receiveCts.Cancel();
                }
                catch
                {
                }
            }

            if (socket != null)
            {
                try
                {
                    if (socket.State ==
                        WebSocketState.Open)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Client disconnect",
                            CancellationToken.None);
                    }
                }
                catch
                {
                }
            }

            if (receiveCts != null)
            {
                try
                {
                    receiveCts.Dispose();
                }
                catch
                {
                }
            }

            if (socket != null)
            {
                try
                {
                    socket.Dispose();
                }
                catch
                {
                }
            }

            Status?.Invoke(
                "OpenAI Realtime disconnected.");
        }

        // =========================================================
        // DISPOSE
        // =========================================================

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            CancellationTokenSource receiveCts;
            ClientWebSocket socket;

            lock (_connectionLock)
            {
                _connectionGeneration++;

                receiveCts =
                    _receiveCts;

                socket =
                    _socket;

                _receiveCts = null;
                _socket = null;
            }

            try
            {
                receiveCts?.Cancel();
            }
            catch
            {
            }

            try
            {
                socket?.Dispose();
            }
            catch
            {
            }

            try
            {
                receiveCts?.Dispose();
            }
            catch
            {
            }

            try
            {
                _sendLock.Dispose();
            }
            catch
            {
            }
        }
    }
}