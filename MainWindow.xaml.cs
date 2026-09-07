using System;
using System.Windows;
using WpfVoiceAgent.Agent;
using WpfVoiceAgent.Audio;
using WpfVoiceAgent.OpenAI;
using WpfVoiceAgent.WakeWord;

namespace WpfVoiceAgent
{
    public partial class MainWindow : Window
    {
        private VoiceAgentController _agent;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("START BUTTON CLICKED");

                if (_agent != null)
                {
                    System.Diagnostics.Debug.WriteLine("AGENT ALREADY EXISTS");
                    return;
                }

                var capture =
    new WasapiAudioCapture();

                var aec =
                    new AdaptiveAecProcessor();

                var wakeWord =
                    new KeywordWakeWordDetector(
                        capture.WaveFormat.SampleRate);

                var realtime =
                    new OpenAiRealtimeClient();

                var playback =
                    new RealtimeAudioPlayback(aec);

                _agent =
                    new VoiceAgentController(
                        capture,
                        aec,
                        wakeWord,
                        realtime,
                        playback);

                _agent.StateChanged += state =>
                    Dispatcher.Invoke(() =>
                        StateText.Text = state.ToString());

                _agent.StatusChanged += status =>
                    Dispatcher.Invoke(() =>
                        StatusText.Text = status);

                _agent.TranscriptReceived += text =>
                    Dispatcher.Invoke(() =>
                    {
                        ConversationBox.AppendText(text);
                        ConversationBox.ScrollToEnd();
                    });


                _agent.Error += error =>
                    Dispatcher.Invoke(() =>
                        ErrorText.Text = error
                        );

                System.Diagnostics.Debug.WriteLine(
                    "Calling agent.Start()...");

                _agent.Start();

                System.Diagnostics.Debug.WriteLine(
                    "agent.Start() returned");

                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "START ERROR: " + ex);

                ErrorText.Text = ex.ToString();
            }
        }
        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            StopAgent();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopAgent();
        }

        private void StopAgent()
        {
            try
            {
                _agent?.Dispose();
                _agent = null;
            }
            catch { }

            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            StateText.Text = "Idle";
        }
    }
}
