using CryptoManager.Net.Data;
using System.Text.Json.Serialization;

namespace CryptoManager.Net.Websockets
{
    internal class WebsocketStatusUpdate
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
        [JsonPropertyName("info")]
        public string? Info { get; set; }
        [JsonPropertyName("topic"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Topic { get; set; }
        [JsonPropertyName("exchange"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? Exchange { get; set; }
        [JsonPropertyName("status")]
        public StreamStatus Status { get; set; }
    }
}
