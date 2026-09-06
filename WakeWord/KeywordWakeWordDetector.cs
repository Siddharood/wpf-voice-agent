using System;
using System.Configuration;
using System.Globalization;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;

namespace WpfVoiceAgent.WakeWord
{
    public sealed class KeywordWakeWordDetector : IWakeWordDetector
    {
        private readonly SpeechRecognitionEngine _recognizer;
        private readonly string _wakePhrase;

        private readonly object _gate = new object();

        private bool _wakeDetected;
        private bool _enabled = true;
        private bool _disposed;

        public KeywordWakeWordDetector(int sampleRate)
        {
            _wakePhrase =
                ConfigurationManager.AppSettings["WakePhrase"]
                ?? "Computer";

            if (string.IsNullOrWhiteSpace(_wakePhrase))
                _wakePhrase = "Computer";

            System.Diagnostics.Debug.WriteLine(
                "WAKE PHRASE CONFIGURED: [" +
                _wakePhrase +
                "]");

            _recognizer =
                new SpeechRecognitionEngine(
                    new CultureInfo("en-GB"));

            _recognizer.SpeechRecognized +=
                Recognizer_SpeechRecognized;

            _recognizer.SpeechRecognitionRejected +=
                Recognizer_SpeechRecognitionRejected;

            _recognizer.RecognizeCompleted +=
                Recognizer_RecognizeCompleted;

            _recognizer.AudioStateChanged +=
                Recognizer_AudioStateChanged;

            var choices =
                new Choices(_wakePhrase);

            var grammarBuilder =
                new GrammarBuilder();

            grammarBuilder.Append(choices);

            var grammar =
                new Grammar(grammarBuilder)
                {
                    Name = "WakeWord"
                };

            _recognizer.LoadGrammar(grammar);

            System.Diagnostics.Debug.WriteLine(
                "WAKE GRAMMAR LOADED: [" +
                _wakePhrase +
                "]");

            System.Diagnostics.Debug.WriteLine(
                "SETTING SAPI INPUT TO DEFAULT AUDIO DEVICE");

            _recognizer.SetInputToDefaultAudioDevice();

            System.Diagnostics.Debug.WriteLine(
                "SAPI DEFAULT AUDIO DEVICE INPUT SET");

            System.Diagnostics.Debug.WriteLine(
                "STARTING SAPI RECOGNITION");

            _recognizer.RecognizeAsync(
                RecognizeMode.Multiple);

            System.Diagnostics.Debug.WriteLine(
                "SAPI RECOGNITION STARTED");
        }

        public void SetEnabled(bool enabled)
        {
            lock (_gate)
            {
                _enabled = enabled;

                /*
                 * Clear any wake event that may have been
                 * generated while wake-word detection was disabled.
                 */
                if (!enabled)
                    _wakeDetected = false;
            }

            System.Diagnostics.Debug.WriteLine(
                "WAKE DETECTOR ENABLED: " + enabled);
        }

        public Task<bool> DetectAsync(
            byte[] pcm,
            CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return Task.FromException<bool>(
                    new ObjectDisposedException(
                        nameof(KeywordWakeWordDetector)));
            }

            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (!_enabled)
                    return Task.FromResult(false);

                if (_wakeDetected)
                {
                    _wakeDetected = false;

                    System.Diagnostics.Debug.WriteLine(
                        "WAKE WORD DETECTED!");

                    return Task.FromResult(true);
                }
            }

            return Task.FromResult(false);
        }

        private void Recognizer_SpeechRecognized(
            object sender,
            SpeechRecognizedEventArgs e)
        {
            if (e == null || e.Result == null)
                return;

            System.Diagnostics.Debug.WriteLine(
                "SPEECH RECOGNIZED: [" +
                e.Result.Text +
                "] Confidence=" +
                e.Result.Confidence);

            lock (_gate)
            {
                /*
                 * SAPI continues listening to the microphone even
                 * when the agent is processing or speaking.
                 *
                 * Ignore all recognition while the wake detector
                 * is disabled.
                 */
                if (!_enabled)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "Recognition ignored because wake detector is disabled.");

                    return;
                }

                if (e.Result.Confidence < 0.60)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "Recognition rejected because confidence was below 0.60.");

                    return;
                }

                string recognized =
                    e.Result.Text == null
                        ? string.Empty
                        : e.Result.Text.Trim();

                if (!string.Equals(
                    recognized,
                    _wakePhrase,
                    StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "Recognized text did not match wake phrase: [" +
                        recognized +
                        "]");

                    return;
                }

                _wakeDetected = true;
            }

            System.Diagnostics.Debug.WriteLine(
                "WAKE PHRASE MATCHED: [" +
                _wakePhrase +
                "]");
        }

        private void Recognizer_SpeechRecognitionRejected(
            object sender,
            SpeechRecognitionRejectedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine(
                "SPEECH RECOGNITION REJECTED");

            if (e != null && e.Result != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "Rejected confidence: " +
                    e.Result.Confidence);
            }
        }

        private void Recognizer_RecognizeCompleted(
            object sender,
            RecognizeCompletedEventArgs e)
        {
            if (e != null && e.Error != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "SAPI RECOGNITION ERROR: " +
                    e.Error);
            }

            if (e != null && e.Cancelled)
            {
                System.Diagnostics.Debug.WriteLine(
                    "SAPI RECOGNITION CANCELLED");
            }

            System.Diagnostics.Debug.WriteLine(
                "SAPI RECOGNITION COMPLETED");
        }

        private void Recognizer_AudioStateChanged(
            object sender,
            AudioStateChangedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine(
                "SAPI AUDIO STATE: " +
                e.AudioState);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            System.Diagnostics.Debug.WriteLine(
                "DISPOSING WAKE DETECTOR");

            try
            {
                _recognizer.RecognizeAsyncCancel();
            }
            catch
            {
            }

            try
            {
                _recognizer.RecognizeAsyncStop();
            }
            catch
            {
            }

            try
            {
                _recognizer.Dispose();
            }
            catch
            {
            }
        }
    }
}