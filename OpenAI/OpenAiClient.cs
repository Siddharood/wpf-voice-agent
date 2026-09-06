using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WpfVoiceAgent.OpenAI
{
    public sealed class OpenAiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _chatModel;
        private readonly string _transcriptionModel;
        private readonly string _ttsModel;
        private readonly string _ttsVoice;

        public OpenAiClient()
        {
            string key = "";
            
            if (string.IsNullOrWhiteSpace(key))
                key = ConfigurationManager.AppSettings["OpenAIApiKey"];

            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException(
                    "OPENAI_API_KEY is not configured. Set the environment variable or App.config.");

            _chatModel = ConfigurationManager.AppSettings["OpenAIChatModel"] ?? "gpt-4o-mini";
            _transcriptionModel = ConfigurationManager.AppSettings["OpenAITranscriptionModel"] ?? "gpt-4o-mini-transcribe";
            _ttsModel = ConfigurationManager.AppSettings["OpenAITtsModel"] ?? "gpt-4o-mini-tts";
            _ttsVoice = ConfigurationManager.AppSettings["OpenAITtsVoice"] ?? "alloy";

            _http = new HttpClient();
            _http.BaseAddress = new Uri("https://api.openai.com/v1/");
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", key);
            _http.Timeout = TimeSpan.FromSeconds(90);
        }

        public async Task<string> TranscribeWavAsync(byte[] wav, CancellationToken ct)
        {
            using (var form = new MultipartFormDataContent())
            using (var file = new ByteArrayContent(wav))
            {
                file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(file, "file", "audio.wav");
                form.Add(new StringContent(_transcriptionModel), "model");
                form.Add(new StringContent("en"), "language");
                form.Add(new StringContent("json"), "response_format");

                using (HttpResponseMessage response =
                    await _http.PostAsync("audio/transcriptions", form, ct).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    EnsureSuccess(response, body);

                    var result = JsonConvert.DeserializeObject<TranscriptionResponse>(body);
                    return result?.text ?? string.Empty;
                }
            }
        }

        public async Task<string> ChatAsync(
            IList<Dictionary<string, string>> messages,
            CancellationToken ct)
        {
            var payload = new
            {
                model = _chatModel,
                messages = messages
            };

            string json = JsonConvert.SerializeObject(payload);

            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response =
                await _http.PostAsync("chat/completions", content, ct).ConfigureAwait(false))
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                EnsureSuccess(response, body);

                var result = JsonConvert.DeserializeObject<ChatResponse>(body);

                if (result?.choices == null || result.choices.Count == 0)
                    return string.Empty;

                return result.choices[0].message?.content ?? string.Empty;
            }
        }

        public async Task<byte[]> TextToSpeechAsync(string text, CancellationToken ct)
        {
            var payload = new
            {
                model = _ttsModel,
                voice = _ttsVoice,
                input = text,
                response_format = "wav"
            };

            string json = JsonConvert.SerializeObject(payload);

            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response =
                await _http.PostAsync("audio/speech", content, ct).ConfigureAwait(false))
            {
                byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        "OpenAI TTS failed: " + Encoding.UTF8.GetString(body));

                return body;
            }
        }

        private static void EnsureSuccess(HttpResponseMessage response, string body)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    "OpenAI request failed (" + (int)response.StatusCode + "): " + body);
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        private sealed class TranscriptionResponse
        {
            public string text { get; set; }
        }

        private sealed class ChatResponse
        {
            public List<Choice> choices { get; set; }
        }

        private sealed class Choice
        {
            public Message message { get; set; }
        }

        private sealed class Message
        {
            public string role { get; set; }
            public string content { get; set; }
        }
    }
}
