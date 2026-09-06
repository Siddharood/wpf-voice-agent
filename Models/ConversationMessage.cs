namespace WpfVoiceAgent.Models
{
    public sealed class ConversationMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }

        public ConversationMessage() { }

        public ConversationMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }
}
