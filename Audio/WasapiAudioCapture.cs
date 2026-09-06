using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WpfVoiceAgent.Audio
{
    public sealed class WasapiAudioCapture : IAudioCapture
    {
        private readonly WasapiCapture _capture;

        public WaveFormat WaveFormat
        {
            get { return _capture.WaveFormat; }
        }

        public event EventHandler<WaveInEventArgs> DataAvailable;

        public WasapiAudioCapture()
        {
            var enumerator = new MMDeviceEnumerator();

            var device = enumerator.GetDefaultAudioEndpoint(
                DataFlow.Capture,
                Role.Communications);

            Debug.WriteLine(
                "CAPTURE DEVICE: " + device.FriendlyName);

            _capture = new WasapiCapture(device);

            _capture.ShareMode = AudioClientShareMode.Shared;

            Debug.WriteLine(
                "CAPTURE FORMAT: " + _capture.WaveFormat);

            _capture.DataAvailable += Capture_DataAvailable;
        }

        private void Capture_DataAvailable(object sender, WaveInEventArgs e)
        {
            Debug.WriteLine(
                "NAUDIO DATA AVAILABLE: " +
                e.BytesRecorded +
                " bytes");

            DataAvailable?.Invoke(this, e);
        }

        public void Start()
        {
            Debug.WriteLine("NAUDIO CAPTURE START");
            _capture.StartRecording();
        }

        public void Stop()
        {
            Debug.WriteLine("NAUDIO CAPTURE STOP");
            _capture.StopRecording();
        }

        public void Dispose()
        {
            _capture.DataAvailable -= Capture_DataAvailable;
            _capture.Dispose();
        }
    }
}