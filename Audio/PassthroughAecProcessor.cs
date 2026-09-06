namespace WpfVoiceAgent.Audio
{
    // Development fallback only.
    //
    // IMPORTANT:
    // This is NOT acoustic echo cancellation. It exists so the application can run
    // without a native AEC dependency. For the assessment, replace this class with
    // a WebRTC AudioProcessing implementation or a Windows AEC APO/AudioClient
    // implementation and feed it the microphone + render reference.
    public sealed class PassthroughAecProcessor : IAecProcessor
    {
        public byte[] ProcessCapture(byte[] microphonePcm)
        {
            return microphonePcm;
        }

        public void PushPlaybackReference(byte[] pcm)
        {
        }

        public void Reset()
        {
        }
    }
}
