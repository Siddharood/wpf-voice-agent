using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;
using WpfVoiceAgent.Agent;
using WpfVoiceAgent.Audio;
using WpfVoiceAgent.OpenAI;
using WpfVoiceAgent.WakeWord;

namespace WpfVoiceAgent
{
    public partial class WebViewWindow : Window
    {
        private VoiceAgentController _agent;
        private bool _webViewReady;

        public WebViewWindow()
        {
            InitializeComponent();

            Loaded += WebViewWindow_Loaded;
        }

        private async void WebViewWindow_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                await InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.ToString(),
                    "WebView2 Initialization Error");
            }
        }

        private async Task InitializeWebViewAsync()
        {
            await WebView.EnsureCoreWebView2Async();

            WebView.CoreWebView2.WebMessageReceived +=
                CoreWebView2_WebMessageReceived;

            string webFolder =
                Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "WebUI");

            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "voiceagent.local",
                webFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            WebView.CoreWebView2.Navigate(
                "https://voiceagent.local/index.html");

            _webViewReady = true;
        }

        private void CoreWebView2_WebMessageReceived(
            object sender,
            CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message =
                    e.TryGetWebMessageAsString();

                JObject json =
                    JObject.Parse(message);

                string type =
                    json["type"]?.ToString();

                if (type == "command")
                {
                    string command =
                        json["command"]?.ToString();

                    if (command == "start")
                    {
                        StartAgent();
                    }
                    else if (command == "stop")
                    {
                        StopAgent();
                    }
                }
            }
            catch (Exception ex)
            {
                SendToWeb(new
                {
                    type = "error",
                    message = ex.Message
                });
            }
        }

        private void StartAgent()
        {
            try
            {
                if (_agent != null)
                    return;

                var capture =
                    new WasapiAudioCapture();

                var aec =
                    new AdaptiveAecProcessor();

                var playback =
                    new WasapiAudioPlayback(aec);

                var wakeWord =
                    new KeywordWakeWordDetector(
                        capture.WaveFormat.SampleRate);

                var openAi =
                    new OpenAiClient();

                _agent =
                    new VoiceAgentController(
                        capture,
                        playback,
                        aec,
                        wakeWord,
                        openAi);

                _agent.StateChanged +=
                    Agent_StateChanged;

                _agent.Status +=
                    Agent_Status;

                _agent.Transcript +=
                    Agent_Transcript;

                _agent.Error +=
                    Agent_Error;

                _agent.Start();

                SendToWeb(new
                {
                    type = "agent",
                    eventName = "started"
                });
            }
            catch (Exception ex)
            {
                SendToWeb(new
                {
                    type = "error",
                    message = ex.ToString()
                });
            }
        }

        private void StopAgent()
        {
            try
            {
                if (_agent == null)
                    return;

                _agent.Dispose();
                _agent = null;

                SendToWeb(new
                {
                    type = "agent",
                    eventName = "stopped"
                });
            }
            catch (Exception ex)
            {
                SendToWeb(new
                {
                    type = "error",
                    message = ex.ToString()
                });
            }
        }

        private void Agent_StateChanged(
            WpfVoiceAgent.Models.AgentState state)
        {
            Dispatcher.Invoke(() =>
            {
                SendToWeb(new
                {
                    type = "state",
                    value = state.ToString()
                });
            });
        }

        private void Agent_Status(string status)
        {
            Dispatcher.Invoke(() =>
            {
                SendToWeb(new
                {
                    type = "status",
                    value = status
                });
            });
        }

        private void Agent_Transcript(string text)
        {
            Dispatcher.Invoke(() =>
            {
                string speaker = "agent";

                if (text.StartsWith("You:"))
                    speaker = "user";

                SendToWeb(new
                {
                    type = "transcript",
                    speaker = speaker,
                    text = text
                });
            });
        }

        private void Agent_Error(string error)
        {
            Dispatcher.Invoke(() =>
            {
                SendToWeb(new
                {
                    type = "error",
                    message = error
                });
            });
        }

        private void SendToWeb(object message)
        {
            if (!_webViewReady ||
                WebView.CoreWebView2 == null)
            {
                return;
            }

            string json =
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    message);

            WebView.CoreWebView2.PostWebMessageAsString(
                json);
        }

        private void Window_Closing(
            object sender,
            System.ComponentModel.CancelEventArgs e)
        {
            StopAgent();
        }
    }
}