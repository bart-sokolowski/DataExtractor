using System.Text.Json.Serialization;

namespace DataExtractor.Models;

public class ExtractedListing
{
    public string Url { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Description { get; set; }

    public List<string> Images { get; set; } = new();

    public string? Price { get; set; }

    public string? Rating { get; set; }

    public string? Location { get; set; }

    public string? Occupancy { get; set; }


    public string? MapImageUrl { get; set; }

    public string? MapLink { get; set; }

    [JsonIgnore]
    public bool HasImages => Images.Count > 0;

    [JsonIgnore]
    public bool HasMap => !string.IsNullOrWhiteSpace(MapImageUrl) || !string.IsNullOrWhiteSpace(MapLink);
}
