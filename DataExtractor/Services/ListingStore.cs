using System.Text.Encodings.Web;
using System.Text.Json;
using DataExtractor.Models;

namespace DataExtractor.Services;

/// <summary>
/// File-backed store for extracted listings so results survive between visits
/// and app restarts. Single-user tool, so a JSON file under App_Data is enough.
/// </summary>
public sealed class ListingStore
{
    // Relaxed escaping keeps URLs readable in the file (no & for ampersands);
    // the file is local data, never served to a browser.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly ILogger<ListingStore> _logger;
    private List<ExtractedListing>? _listings;

    public ListingStore(IWebHostEnvironment environment, ILogger<ListingStore> logger)
    {
        _filePath = Path.Combine(environment.ContentRootPath, "App_Data", "listings.json");
        _logger = logger;
    }

    public IReadOnlyList<ExtractedListing> GetAll()
    {
        lock (_gate)
        {
            return Load().ToList();
        }
    }

    public void AddOrUpdate(IEnumerable<ExtractedListing> items)
    {
        lock (_gate)
        {
            var listings = Load();
            foreach (var item in items)
            {
                var existingIndex = listings.FindIndex(l => string.Equals(l.Url, item.Url, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                    listings.RemoveAt(existingIndex);
            }

            // Newest extraction first.
            listings.InsertRange(0, items);
            Save(listings);
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var listings = Load();
            var removed = listings.RemoveAll(l => l.Id == id) > 0;
            if (removed)
                Save(listings);
            return removed;
        }
    }

    private List<ExtractedListing> Load()
    {
        if (_listings != null)
            return _listings;

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _listings = JsonSerializer.Deserialize<List<ExtractedListing>>(json) ?? new List<ExtractedListing>();
            }
            else
            {
                _listings = new List<ExtractedListing>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load listings from {Path}; starting empty", _filePath);
            _listings = new List<ExtractedListing>();
        }

        return _listings;
    }

    private void Save(List<ExtractedListing> listings)
    {
        _listings = listings;

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(listings, SerializerOptions));
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
