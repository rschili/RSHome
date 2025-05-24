using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace RSHome.Services;

public interface IToolService
{
    Task<string> GetCurrentWeatherAsync(string location);
}

public class ToolService: IToolService
{
    public IConfigService Config { get; private init; }
    public ILogger Logger { get; private init; }
    public IHttpClientFactory HttpClientFactory { get; private init; }

    public ToolService(IConfigService config, ILogger<ToolService> logger, IHttpClientFactory httpClientFactory)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        HttpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public async Task<string> GetCurrentWeatherAsync(string location)
    {
        if (string.IsNullOrWhiteSpace(location) || location.Length > 100)
            throw new ArgumentException("Location cannot be null or empty and must not exceed 100 characters.", nameof(location));

        using var httpClient = HttpClientFactory.CreateClient();
        string url = $"https://api.openweathermap.org/data/2.5/weather?q={Uri.EscapeDataString(location)}&appid={Config.OpenWeatherMapApiKey}&units=metric&lang=de";
        HttpResponseMessage response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        string responseBody = await response.Content.ReadAsStringAsync();
        using JsonDocument jsonDoc = JsonDocument.Parse(responseBody);
        JsonElement root = jsonDoc.RootElement;

        // Extract relevant information
        try
        {
            var weather = JsonSerializer.Deserialize<WeatherResponse>(responseBody);

            if (weather == null)
                throw ThrowWeatherApiException(responseBody);

            string cityName = weather.Name;
            string description = weather.Weather?[0]?.Description ?? "";
            double temperature = weather.Main.Temp;
            double feelsLike = weather.Main.FeelsLike;
            int humidity = weather.Main.Humidity;
            double windSpeed = weather.Wind.Speed;
            string country = weather.Sys.Country;
            long sunriseUnix = weather.Sys.Sunrise;
            long sunsetUnix = weather.Sys.Sunset;

            // Convert sunrise and sunset from Unix time to local time
            DateTimeOffset sunrise = DateTimeOffset.FromUnixTimeSeconds(sunriseUnix).ToLocalTime();
            DateTimeOffset sunset = DateTimeOffset.FromUnixTimeSeconds(sunsetUnix).ToLocalTime();

            // Format the summary
            return $"Aktuelles Wetter in {cityName}, {country}: {description}, {temperature}°C (gefühlt {feelsLike}°C), Luftfeuchtigkeit: {humidity}%, Wind: {windSpeed} m/s, Sonnenaufgang: {sunrise:HH:mm}, Sonnenuntergang: {sunset:HH:mm}";
        }
        catch (JsonException)
        {
            throw ThrowWeatherApiException(responseBody);
        }
    }

    private Exception ThrowWeatherApiException(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            string code = root.TryGetProperty("cod", out var codElement) ? codElement.ToString() : "unknown";
            string message = root.TryGetProperty("message", out var msgElement) ? msgElement.GetString() ?? "No message" : "No message";
            Logger.LogError("Weather API error: {Code} - {Message}", code, message);
            return new Exception($"Weather API error: {message}");
        }
        catch
        {
            Logger.LogError("Failed to parse error response from Weather API: {ResponseBody}", responseBody);
            return new Exception("Unknown error from Weather API");
        }
    }

    public class WeatherResponse
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("weather")]
        public required List<WeatherInfo> Weather { get; set; }

        [JsonPropertyName("main")]
        public required MainWeather Main { get; set; }

        [JsonPropertyName("wind")]
        public required Wind Wind { get; set; }

        [JsonPropertyName("sys")]
        public required Sys Sys { get; set; }
    }

    public class WeatherInfo
    {
        [JsonPropertyName("description")]
        public required string Description { get; set; }
    }

    public class MainWeather
    {
        [JsonPropertyName("temp")]
        public required double Temp { get; set; }

        [JsonPropertyName("feels_like")]
        public required double FeelsLike { get; set; }

        [JsonPropertyName("humidity")]
        public required int Humidity { get; set; }
    }

    public class Wind
    {
        [JsonPropertyName("speed")]
        public required double Speed { get; set; }
    }

    public class Sys
    {
        [JsonPropertyName("country")]
        public required string Country { get; set; }

        [JsonPropertyName("sunrise")]
        public required long Sunrise { get; set; }

        [JsonPropertyName("sunset")]
        public required long Sunset { get; set; }
    }
}
