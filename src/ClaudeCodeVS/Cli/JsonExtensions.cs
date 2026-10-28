using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVS.Cli
{
    /// <summary>Lenient accessors for the loosely-typed messages the CLI emits.</summary>
    internal static class JsonExtensions
    {
        public static string Str(this JToken token, string key)
        {
            var value = (token as JObject)?[key];
            if (value == null || value.Type == JTokenType.Null)
            {
                return null;
            }

            return value.Type == JTokenType.String ? (string)value : value.ToString(Formatting.None);
        }

        public static JObject Obj(this JToken token, string key) => (token as JObject)?[key] as JObject;

        public static JArray Arr(this JToken token, string key) => (token as JObject)?[key] as JArray;

        public static bool Bool(this JToken token, string key)
        {
            var value = (token as JObject)?[key];
            return value != null && value.Type == JTokenType.Boolean && (bool)value;
        }

        public static double? Num(this JToken token, string key)
        {
            var value = (token as JObject)?[key];
            return value != null && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
                ? (double)value
                : (double?)null;
        }
    }
}
