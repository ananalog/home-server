using System.Text.Json.Serialization;
using Home.Client;

namespace Home.Server;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<PointDto>))]
internal sealed partial class ServerJson : JsonSerializerContext;
